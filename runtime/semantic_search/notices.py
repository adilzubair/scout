"""Retain installed dependency notices and native evidence; never grant release approval."""
import argparse
import base64
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import sys
import uuid

POLICY=Path(__file__).with_name('distribution.json')
MAX_FILE=2*1024**2
MAX_TOTAL=64*1024**2

def owned(root,relative):
    relative=str(relative).replace('\\','/')
    parts=PurePosixPath(str(relative))
    if parts.is_absolute() or not parts.parts or any(part in ('..','.') for part in parts.parts) or any(c in str(relative) for c in (':','\0')):
        raise ValueError('invalid_notice_path')
    root=Path(root).absolute();target=root.joinpath(*parts.parts)
    for path in (target,*target.parents):
        if path.is_symlink() or path.is_junction():raise ValueError('linked_notice_path')
    return target

def read(root,relative):
    path=owned(root,relative)
    with path.open('rb') as stream:data=stream.read(MAX_FILE+1)
    if len(data)>MAX_FILE:raise ValueError('notice_file_budget')
    return data

def sha(data):return hashlib.sha256(data).hexdigest()

def native_audit(site):
    policy=json.loads(POLICY.read_text(encoding='utf-8'))
    if importlib.metadata.version('av')!=policy['pyavVersion']:raise ValueError('unexpected_pyav_version')
    actual=[]
    for path in sorted(owned(site,'av.libs').iterdir()):
        if path.suffix.lower()!='.dll':continue
        path=owned(site,'av.libs/'+path.name)
        with path.open('rb') as stream:digest=hashlib.file_digest(stream,'sha256').hexdigest()
        actual.append(dict(name=path.name,bytes=path.stat().st_size,sha256=digest))
    if actual!=policy['referenceNativeFiles']:raise ValueError('native_reference_mismatch')
    import av
    return dict(scope='PyAV and bundled FFmpeg libraries only',pyavVersion=av.__version__,wheelMetadata=read(site,'av-'+av.__version__+'.dist-info/WHEEL').decode('utf-8'),
        ffmpegBuild=av._core.library_meta,nativeFiles=actual,policySha256=sha(POLICY.read_bytes()),
        candidateBuildRecipe=policy['candidateBuildRecipe'],distributionApproved=False,reviewFindings=policy['reviewFindings'])

def collect(generation,site=None,python_home=None):
    generation=Path(generation).absolute()
    site=Path(site or Path(sys.prefix)/'Lib/site-packages').absolute()
    if site!=generation/'venv/Lib/site-packages':raise ValueError('notice_environment_outside_generation')
    owned(generation,'notices/check')
    native=native_audit(site) # trusted pinned runtime only; no user-content decoding
    policy=json.loads(POLICY.read_text(encoding='utf-8'))
    files={};packages=[];findings=[];total=0
    def retain(relative,data):
        nonlocal total
        if relative in files:raise ValueError('duplicate_notice_output')
        total+=len(data)
        if total>MAX_TOTAL or len(files)>=4096:raise ValueError('notice_total_budget')
        files[relative]=data
        return dict(file=relative,bytes=len(data),sha256=sha(data))
    distributions=sorted(importlib.metadata.distributions(path=[str(site)]),key=lambda item:(item.metadata['Name'] or '').lower())
    if not 1<=len(distributions)<=512:raise ValueError('notice_package_budget')
    identities=set()
    for distribution in distributions:
        name=distribution.metadata.get('Name','');version=distribution.version
        normalized=re.sub('[-_.]+','-',name).lower()
        if not re.fullmatch('[a-z0-9][a-z0-9-]{0,199}',normalized) or not re.fullmatch('[a-zA-Z0-9.+_-]{1,80}',version):raise ValueError('invalid_notice_identity')
        identity=normalized+'-'+version
        if identity in identities:raise ValueError('duplicate_notice_identity')
        identities.add(identity)
        records=list(distribution.files or [])
        by_name={str(entry).replace('\\','/'):entry for entry in records}
        if len(by_name)!=len(records):raise ValueError('duplicate_notice_record')
        entries=[PurePosixPath(entry) for entry in by_name]
        metadata=[entry for entry in entries if len(entry.parts)==2 and entry.parts[0].endswith('.dist-info') and entry.parts[1]=='METADATA']
        if len(metadata)!=1:raise ValueError('missing_notice_metadata_record')
        folder=metadata[0].parts[0];selected={str(metadata[0])}
        declared=distribution.metadata.get_all('License-File',[])
        for relative in declared:
            # Both PEP 639 and older wheel layouts are supported; traversal never is.
            relative=relative.replace('\\','/')
            candidates=[str(PurePosixPath(folder)/'licenses'/relative),str(PurePosixPath(folder)/relative)]
            for candidate in candidates:owned(site,candidate)
            found=next((candidate for candidate in candidates if candidate in by_name),None)
            if found is None:findings.append('declared_notice_missing:'+normalized)
            else:selected.add(found)
        for entry in entries:
            if not entry.parts or entry.parts[0]!=folder:continue
            if 'licenses' in [part.lower() for part in entry.parts[1:-1]] or re.match('(?i)^(license|licence|copying|copyright|notice|authors)([._-]|$)',entry.name):selected.add(str(entry))
        retained=[]
        for relative in sorted(selected):
            data=read(site,relative);entry=by_name[relative]
            if entry.hash is not None:
                if entry.hash.mode!='sha256' or base64.urlsafe_b64encode(hashlib.sha256(data).digest()).decode().rstrip('=')!=entry.hash.value:
                    raise ValueError('notice_record_hash_mismatch')
            else:findings.append('notice_record_hash_missing:'+normalized)
            tail=PurePosixPath(relative).relative_to(folder).as_posix()
            retained.append(retain('packages/'+identity+'/'+tail,data))
        supplemental=policy.get('supplementalNotices',{}).get(normalized)
        if supplemental is not None:
            if supplemental['version']!=version:raise ValueError('supplemental_notice_version_mismatch')
            data=read(POLICY.parent.parent,supplemental['file'])
            if sha(data)!=supplemental['sha256']:raise ValueError('supplemental_notice_hash_mismatch')
            retained.append({**retain('packages/'+identity+'/UPSTREAM-LICENSE.txt',data),'source':supplemental['source']})
        if len(selected)==1 and supplemental is None:findings.append('license_text_missing:'+normalized)
        packages.append(dict(name=name,version=version,licenseDeclaration=(distribution.metadata.get('License-Expression') or distribution.metadata.get('License') or '')[:2000],notices=retained))
    home=Path(python_home or sys.base_prefix)
    interpreter=retain('python/LICENSE.txt',read(home,'LICENSE.txt'))
    model_notice=policy.get('modelNotice');model=None
    if model_notice is not None:
        manifest=json.loads(read(POLICY.parent.parent,'embeddinggemma2_model.json'))
        if any(manifest[key]!=model_notice[key] for key in ('repository','revision','license')):raise ValueError('model_notice_identity_mismatch')
        data=read(POLICY.parent.parent,model_notice['licenseFile'])
        if sha(data)!=model_notice['licenseSha256']:raise ValueError('model_notice_hash_mismatch')
        model={**model_notice,'retainedLicense':retain('model/Apache-2.0.txt',data)}
    report=dict(schemaVersion=1,purpose='Installed notice/provenance inventory; not distribution approval',
        distributionApproved=False,pythonVersion=platform.python_version(),pythonLicense=interpreter,
        model=model,packages=packages,native=native,reviewFindings=sorted(set(findings)),retainedBytes=total)
    parent=owned(generation,'notices');parent.mkdir(exist_ok=True)
    bundle=owned(parent,'bundle-'+uuid.uuid4().hex);bundle.mkdir()
    for relative,data in files.items():
        target=owned(bundle,relative);target.parent.mkdir(parents=True,exist_ok=True)
        with target.open('xb') as stream:stream.write(data);stream.flush();os.fsync(stream.fileno())
    manifest=json.dumps(report,indent=2,ensure_ascii=False).encode('utf-8')
    with owned(bundle,'manifest.json').open('xb') as stream:stream.write(manifest);stream.flush();os.fsync(stream.fileno())
    pointer=dict(schemaVersion=1,manifest=bundle.name+'/manifest.json',bytes=len(manifest),sha256=sha(manifest),distributionApproved=False)
    temporary=owned(parent,'current-'+uuid.uuid4().hex+'.partial')
    try:
        with temporary.open('x',encoding='utf-8') as stream:json.dump(pointer,stream,indent=2);stream.flush();os.fsync(stream.fileno())
        temporary.replace(owned(parent,'current.json'))
    finally:temporary.unlink(missing_ok=True)
    return report

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--generation',type=Path,required=True)
    args=parser.parse_args();report=collect(args.generation)
    print(f"Retained {len(report['packages'])} package notice inventories; distribution/source review remains open.")

if __name__=='__main__':main()
