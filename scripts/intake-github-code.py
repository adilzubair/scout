"""Pinned public Python-code intake for DEVELOPMENT review; never imports source code."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import urllib.parse
import uuid

spec=importlib.util.spec_from_file_location('esc_intake',Path(__file__).with_name('prepare-esc10-evaluation.py'))
esc=importlib.util.module_from_spec(spec);spec.loader.exec_module(esc)
media=esc.media
MAX_SOURCE=256*1024
MAX_LICENSE=64*1024
MAX_TOTAL=8*1024*1024

def read_json(path,limit=2*1024*1024):
    if not 0<path.stat().st_size<=limit:raise ValueError('metadata_size_budget')
    return json.loads(path.read_text(encoding='utf-8-sig'))

def relative(value):
    if not isinstance(value,str) or not 0<len(value)<=240 or any(c in value for c in '\\:\0') or any(ord(c)<32 for c in value) or len(value.split('/'))>12 or any(p in ('','.','..') for p in value.split('/')):
        raise ValueError('invalid_repository_path')
    return value

def sha(value):return isinstance(value,str) and re.fullmatch('[a-f0-9]{40}',value)

def verified_tree(data,expected):
    if not sha(expected) or data.get('sha')!=expected or data.get('truncated') is not False or not isinstance(data.get('tree'),list) or not 0<len(data['tree'])<=5000:raise ValueError('invalid_pinned_tree')
    items={}
    for row in data['tree']:
        name=relative(row['path'])
        if '/' in name or name in items or not sha(row.get('sha')) or (row.get('mode'),row.get('type')) not in {('040000','tree'),('100644','blob'),('100755','blob'),('120000','blob'),('160000','commit')}:
            raise ValueError('invalid_tree_entry')
        items[name]=row
    ordered=sorted(items.values(),key=lambda r:(r['path']+('/' if r['type']=='tree' else '')).encode('utf-8'))
    payload=b''.join(r['mode'].lstrip('0').encode()+b' '+r['path'].encode('utf-8')+b'\0'+bytes.fromhex(r['sha']) for r in ordered)
    if hashlib.sha1(b'tree '+str(len(payload)).encode()+b'\0'+payload).hexdigest()!=expected:raise ValueError('tree_checksum_mismatch')
    return items

def publish(root,name,payload):
    target=media.owned(root,root/name)
    if target.exists():
        if target.read_bytes()!=payload:raise ValueError('existing_provenance_changed')
        return
    target.parent.mkdir(parents=True,exist_ok=True);temporary=target.with_suffix('.'+uuid.uuid4().hex+'.part')
    try:
        with temporary.open('xb') as stream:stream.write(payload);stream.flush();os.fsync(stream.fileno())
        os.link(temporary,target)
    finally:temporary.unlink(missing_ok=True)

def selected(definition,metadata):
    data=read_json(definition,256*1024)
    if data.get('schemaVersion')!=1 or data.get('intendedSplit')!='development' or not isinstance(data.get('repositories'),list) or not 0<len(data['repositories'])<=20:raise ValueError('invalid_development_selection')
    seen=set();total=0
    for row in data['repositories']:
        repo=row['repo']
        if not isinstance(repo,str) or not re.fullmatch('[A-Za-z0-9][A-Za-z0-9_.-]{0,59}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}',repo) or repo.lower() in seen or not sha(row.get('revision')) or not sha(row.get('rootTree')):
            raise ValueError('invalid_pinned_repository')
        seen.add(repo.lower())
        for key in ('files','licenseFiles'):
            if not isinstance(row.get(key),list) or not 0<len(row[key])<=(10 if key=='files' else 4) or any(not isinstance(p,str) for p in row[key]) or len(set(row[key]))!=len(row[key]):raise ValueError('invalid_file_selection')
            for path in row[key]:relative(path)
        if any(Path(p).suffix!='.py' for p in row['files']) or set(row['files'])&set(row['licenseFiles']):raise ValueError('invalid_source_or_license_path')
        pin=row.get('commitMetadataSha256','')
        if not re.fullmatch('[a-f0-9]{64}',pin):raise ValueError('missing_commit_metadata_pin')
        commit_path=media.owned(metadata,metadata/(repo.replace('/','--')+'-commit.json'))
        commit=read_json(commit_path)
        if media.digest(commit_path)!=pin or commit.get('sha')!=row['revision'] or commit.get('tree',{}).get('sha')!=row['rootTree']:raise ValueError('commit_metadata_changed')
        total+=len(row['files'])
    if total>50:raise ValueError('source_count_budget')
    return data

def intake(definition,metadata,output,offline=False):
    definition=Path(definition).resolve();metadata=Path(metadata).resolve();root=Path(os.path.abspath(output));media.owned(root,root/'intake.json');root.mkdir(parents=True,exist_ok=True)
    data=selected(definition,metadata);definition_hash=media.digest(definition);items=[];used=set();total=0;snapshots={}
    with media.owner_lock(root,'.github-code.lock'):
        if any((root/name).exists() for name in ('dataset.json','freeze.json')):raise FileExistsError('reviewed_or_frozen_pool')
        for row in data['repositories']:
            repo=row['repo'];key=hashlib.sha256(repo.lower().encode()).hexdigest()[:12]
            commit_path=metadata/(repo.replace('/','--')+'-commit.json');publish(root,'provenance/'+key+'-commit.json',commit_path.read_bytes())
            def tree(identity):
                name='provenance/trees/'+identity+'.json'
                seed=media.owned(metadata,metadata/('trees/'+identity+'.json'))
                if seed.is_file():publish(root,name,seed.read_bytes())
                raw=esc.cached(root,name,'https://api.github.com/repos/'+repo+'/git/trees/'+identity,offline=offline)
                result=verified_tree(json.loads(raw),identity);snapshots[name]=hashlib.sha256(raw).hexdigest();return result
            def blob(path,limit):
                entries=tree(row['rootTree']);parts=path.split('/')
                for component in parts[:-1]:
                    node=entries.get(component,{})
                    if node.get('type')!='tree' or node.get('mode')!='040000':raise ValueError('source_tree_path_missing_or_linked')
                    entries=tree(node['sha'])
                node=entries.get(parts[-1],{})
                if node.get('type')!='blob' or node.get('mode') not in ('100644','100755') or type(node.get('size')) is not int or not 0<node['size']<=limit:raise ValueError('source_blob_missing_linked_or_size_budget')
                return node
            licenses=[]
            for path in row['licenseFiles']:
                node=blob(path,MAX_LICENSE);name='licenses/'+key+'/'+path
                raw=esc.cached(root,name,'https://raw.githubusercontent.com/'+repo+'/'+row['revision']+'/'+urllib.parse.quote(path,safe='/'),(node['sha'],node['size']),offline)
                raw.decode('utf-8');total+=len(raw)
                if total>MAX_TOTAL:raise ValueError('batch_size_budget')
                licenses.append(dict(path=path,file=name,gitBlobSha1=node['sha'],sha256=hashlib.sha256(raw).hexdigest(),bytes=len(raw)))
            for path in row['files']:
                node=blob(path,MAX_SOURCE)
                if node['sha'] in used:raise ValueError('duplicate_code_blob')
                used.add(node['sha']);identity='gh-'+key+'-'+node['sha'][:12];name='sources/f-'+hashlib.sha256(identity.encode()).hexdigest()[:12]+'.py'
                raw=esc.cached(root,name,'https://raw.githubusercontent.com/'+repo+'/'+row['revision']+'/'+urllib.parse.quote(path,safe='/'),(node['sha'],node['size']),offline)
                if b'\0' in raw:raise ValueError('binary_code_source')
                raw.decode('utf-8');total+=len(raw)
                if total>MAX_TOTAL:raise ValueError('batch_size_budget')
                items.append(dict(id=identity,repo=repo,revision=row['revision'],rootTree=row['rootTree'],originalPath=path,gitBlobSha1=node['sha'],family='code',localFile=name,bytes=len(raw),sha256=hashlib.sha256(raw).hexdigest(),
                    sourceUrl='https://github.com/'+repo+'/blob/'+row['revision']+'/'+urllib.parse.quote(path,safe='/'),licenses=licenses,commitMetadataSha256=row['commitMetadataSha256'],relatedGroupCandidate='gh-repo-'+key,
                    intendedSplit='development',review=dict(content=False,license=False,relatedGroups=False,labelsApproved=False)))
                print('Verified inert code source '+identity,flush=True)
        if media.digest(definition)!=definition_hash or selected(definition,metadata)!=data:raise ValueError('selection_changed_during_intake')
        publish(root,'provenance/selection.json',definition.read_bytes())
        manifest=dict(schemaVersion=1,purpose='Unreviewed natural-code DEVELOPMENT candidates; source files are never imported or executed',intendedSplit='development',selectionSha256=definition_hash,items=items,treeSnapshots=snapshots,embeddingInference='not_run',labelsApproved=False)
        target=root/'intake.json'
        if target.exists():
            if read_json(target)!=manifest:raise ValueError('published_intake_changed')
        else:media.exclusive_json(target,manifest)
        print('Verified '+str(len(items))+' development-only sources; content, rights, grouping and labels still require review.')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--selection',type=Path,required=True);parser.add_argument('--metadata',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--offline',action='store_true')
    args=parser.parse_args();intake(args.selection,args.metadata,args.output,args.offline)
