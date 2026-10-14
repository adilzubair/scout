"""Bounded public-media discovery/intake. Outputs remain unreviewed, not holdout labels."""
import argparse
from contextlib import contextmanager
from datetime import datetime,timezone
import hashlib
import html
import json
import os
from pathlib import Path
import re
import time
import urllib.parse
import urllib.request
import urllib.error
from email.utils import parsedate_to_datetime
import uuid

API='https://commons.wikimedia.org/w/api.php'
USER_AGENT='ScoutLocalEvaluation/0.1 (local content retrieval benchmark)'
MAX_FILE=32*1024**2
MAX_TOTAL=512*1024**2
EXTENSIONS={'images':{'.jpg','.jpeg','.png','.webp','.bmp','.tif','.tiff'},'audio':{'.wav','.mp3','.m4a','.flac','.ogg','.aac'},'video':{'.mp4','.mov','.mkv','.webm'}}
LICENSES={'CC0','Public domain'}|{f'CC BY{suffix} {version}' for suffix in ('','-SA') for version in ('1.0','2.0','2.5','3.0','4.0')}

def digest(path,kind='sha256'):
    with Path(path).open('rb') as stream:return hashlib.file_digest(stream,kind).hexdigest()
def text(value,limit=1000):return html.unescape(re.sub('<[^>]*>',' ',str(value))).strip()[:limit]
def safe_url(value,api=False):
    url=urllib.parse.urlsplit(value)
    hosts={'commons.wikimedia.org'} if api else {'upload.wikimedia.org'}
    if url.scheme!='https' or url.hostname not in hosts or url.port not in (None,443) or url.username or url.password:raise ValueError('unapproved_media_host')
    return urllib.parse.urlunsplit((url.scheme,url.netloc,url.path,url.query if api else '', ''))
class Redirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,request,fp,code,msg,headers,newurl):
        safe_url(newurl,api=urllib.parse.urlsplit(request.full_url).hostname=='commons.wikimedia.org')
        return super().redirect_request(request,fp,code,msg,headers,newurl)
def open_response(request):
    """Retry only explicit temporary HTTP failures, honoring bounded Retry-After."""
    for attempt in range(3):
        try:
            return urllib.request.build_opener(Redirects()).open(request,timeout=30)
        except urllib.error.HTTPError as error:
            delay=error.headers.get('Retry-After','');error.close()
            if error.code not in (429,503) or attempt==2:raise
            try:seconds=float(delay)
            except ValueError:
                try:seconds=max(0,parsedate_to_datetime(delay).timestamp()-time.time())
                except (ValueError,TypeError):seconds=10*(attempt+1)
            if not 0<=seconds<=60:raise
            print(f'Provider rate limit; waiting {max(1,seconds):.0f} seconds before retry.',flush=True);time.sleep(max(1,seconds))

def fetch_json(parameters):
    query=urllib.parse.urlencode(dict(action='query',format='json',**parameters))
    request=urllib.request.Request(safe_url(API+'?'+query,api=True),headers={'User-Agent':USER_AGENT})
    with open_response(request) as response:payload=response.read(4*1024**2+1)
    if len(payload)>4*1024**2:raise ValueError('metadata_budget')
    data=json.loads(payload)
    if 'error' in data:raise ValueError('commons_api_error')
    return list(data.get('query',{}).get('pages',{}).values())
def record(page,family,topic):
    info=page.get('imageinfo',[{}])[0];metadata=info.get('extmetadata',{})
    def field(key):return text(metadata.get(key,{}).get('value',''))
    name=field('LicenseShortName');url=info.get('url','');suffix=Path(urllib.parse.unquote(urllib.parse.urlsplit(url).path)).suffix.lower()
    size=info.get('size',0);sha=info.get('sha1','');mime=info.get('mime','')
    reason=None
    if name not in LICENSES:reason='license_requires_other_review'
    elif suffix not in EXTENSIONS[family]:reason='container_not_selected'
    elif not isinstance(size,int) or not 0<size<=MAX_FILE:reason='source_size_limit'
    elif not re.fullmatch('[0-9a-f]{40}',sha):reason='missing_original_checksum'
    elif not mime.startswith({'images':'image/','audio':('audio/','application/ogg'),'video':'video/'}[family]):reason='media_type_mismatch'
    else:
        try:safe_url(url)
        except ValueError:reason='unapproved_media_host'
    return dict(id='commons-'+str(page['pageid']),pageId=page['pageid'],title=page['title'],family=family,topics=[topic],
                revision=page.get('revisions',[{}])[0].get('revid'),descriptionUrl=info.get('descriptionurl',''),
                original=dict(url=url,bytes=size,sha1=sha,mime=mime,timestamp=info.get('timestamp'),width=info.get('width'),height=info.get('height'),duration=info.get('duration')),
                license=dict(name=name,url=field('LicenseUrl'),artist=field('Artist'),credit=field('Credit'),restrictions=field('Restrictions'),attributionRequired=field('AttributionRequired')),
                caption=field('ImageDescription')[:500],eligibleForIntake=reason is None,rejection=reason,
                review=dict(content=False,license=False,relatedGroups=False,split=None))
def discover(definition,output):
    output=Path(os.path.abspath(output));owned(output.parent,output);output.parent.mkdir(parents=True,exist_ok=True)
    with owner_lock(output.parent,output.name+'.lock'):return discover_locked(definition,output)

def discover_locked(definition,output):
    if output.exists():raise FileExistsError('Refusing to replace discovery evidence')
    spec=json.loads(Path(definition).read_text(encoding='utf-8'));items={};completed=set();partial=output.with_suffix('.partial.json')
    if spec.get('schemaVersion')!=1 or len(spec['queries'])>50:raise ValueError('invalid_discovery_definition')
    common=dict(prop='imageinfo|revisions',iiprop='url|size|sha1|mime|timestamp|extmetadata',rvprop='ids')
    requests=[(query,dict(generator='search',gsrsearch=query['query'],gsrnamespace=6,gsrlimit=min(10,query['limit']),**common)) for query in spec['queries']]
    requests += [(query,dict(titles=query['title'],**common)) for query in spec.get('namedCandidates',[])]
    created=datetime.now(timezone.utc).isoformat();definition_hash=digest(definition)
    if partial.exists():
        prior=json.loads(owned(output.parent,partial).read_text(encoding='utf-8'))
        if prior.get('definitionSha256')!=definition_hash or prior.get('purpose')!='unreviewed candidate-media-pool':raise ValueError('checkpoint_definition_changed')
        items={item['id']:item for item in prior['items']};completed=set(prior['completedTopics']);created=prior['createdUtc']
    def snapshot():return dict(schemaVersion=1,purpose='unreviewed candidate-media-pool',createdUtc=created,definitionSha256=definition_hash,completedTopics=sorted(completed),items=list(items.values()))
    def checkpoint():
        temporary=owned(output.parent,output.with_suffix('.'+uuid.uuid4().hex+'.partial'))
        try:
            with temporary.open('x',encoding='utf-8') as stream:json.dump(snapshot(),stream,indent=2,ensure_ascii=False);stream.flush();os.fsync(stream.fileno())
            temporary.replace(owned(output.parent,partial))
        finally:temporary.unlink(missing_ok=True)
    for query,parameters in requests:
        if query['id'] in completed:continue
        if query['family'] not in EXTENSIONS:raise ValueError('invalid_media_family')
        checkpoint()
        for page in fetch_json(parameters):
            if page.get('pageid',-1)<0 or not page.get('imageinfo'):continue
            item=record(page,query['family'],query['id'])
            if item['id'] in items:
                if query['id'] not in items[item['id']]['topics']:items[item['id']]['topics'].append(query['id'])
            else:items[item['id']]=item
        completed.add(query['id']);checkpoint();print('Discovered '+query['id'],flush=True);time.sleep(2)
    exclusive_json(output,snapshot())
    partial.unlink()
    print(f"Discovery: {len(items)} candidates; {sum(item['eligibleForIntake'] for item in items.values())} eligible for intake")
def owned(root,path):
    root=Path(os.path.abspath(root));path=Path(os.path.abspath(path))
    if root not in path.parents:raise ValueError('intake_path_escape')
    for part in [path,*path.parents]:
        if part.is_symlink() or part.is_junction():raise ValueError('linked_intake_path')
    return path

@contextmanager
def owner_lock(root,name):
    """OS lock survives neither a crash nor close; the empty lock file is reusable."""
    path=owned(root,Path(root)/name)
    with path.open('a+b') as stream:
        if os.name=='nt':
            import msvcrt
            acquire=lambda:msvcrt.locking(stream.fileno(),msvcrt.LK_NBLCK,1)
            release=lambda:msvcrt.locking(stream.fileno(),msvcrt.LK_UNLCK,1)
        else:
            import fcntl
            acquire=lambda:fcntl.flock(stream,fcntl.LOCK_EX|fcntl.LOCK_NB)
            release=lambda:fcntl.flock(stream,fcntl.LOCK_UN)
        stream.seek(0)
        try:acquire()
        except OSError as error:raise RuntimeError('intake_owner_busy') from error
        try:yield
        finally:stream.seek(0);release()

def exclusive_json(path,value):
    path=Path(path);temporary=owned(path.parent,path.with_suffix('.'+uuid.uuid4().hex+'.partial'))
    try:
        with temporary.open('x',encoding='utf-8') as stream:
            json.dump(value,stream,indent=2,ensure_ascii=False);stream.flush();os.fsync(stream.fileno())
        os.link(temporary,path) # atomic publication, fails if final evidence already exists
    finally:temporary.unlink(missing_ok=True)
def download(item,root,existing_only=False):
    original=item['original'];url=safe_url(original['url']);suffix=Path(urllib.parse.unquote(urllib.parse.urlsplit(url).path)).suffix.lower()
    if (not item['eligibleForIntake'] or item['license']['name'] not in LICENSES or
        suffix not in EXTENSIONS.get(item['family'],set()) or type(original['bytes']) is not int or
        not 0<original['bytes']<=MAX_FILE or not re.fullmatch('[0-9a-f]{40}',original['sha1']) or
        not re.fullmatch('commons-[0-9]+',item['id'])):raise ValueError('unapproved_candidate')
    target=owned(root,Path(root)/'sources'/('f-'+hashlib.sha256(item['id'].encode()).hexdigest()[:12]+suffix))
    if target.exists():
        if target.stat().st_size!=original['bytes'] or digest(target,'sha1')!=original['sha1']:raise ValueError('existing_source_changed')
        return dict(file=target.relative_to(root).as_posix(),bytes=target.stat().st_size,sha256=digest(target))
    if existing_only:raise FileNotFoundError('verified_source_not_available_offline')
    target.parent.mkdir(parents=True,exist_ok=True);temporary=owned(root,target.with_suffix('.'+uuid.uuid4().hex+'.part'));began=time.monotonic()
    try:
        request=urllib.request.Request(url,headers={'User-Agent':USER_AGENT});count=0
        with open_response(request) as response,temporary.open('xb') as stream:
            while data:=response.read(65536):
                count+=len(data)
                if count>min(MAX_FILE,original['bytes']) or time.monotonic()-began>120:raise ValueError('download_budget')
                stream.write(data)
        if count!=original['bytes'] or digest(temporary,'sha1')!=original['sha1']:raise ValueError('download_checksum_mismatch')
        temporary.replace(target)
        return dict(file=target.relative_to(root).as_posix(),bytes=count,sha256=digest(target))
    finally:temporary.unlink(missing_ok=True)
def intake(discovery,root,identifiers,existing_only=False):
    root=Path(os.path.abspath(root));owned(root,root/'intake.json');root.mkdir(parents=True,exist_ok=True)
    with owner_lock(root,'.intake.lock'):return intake_locked(discovery,root,identifiers,existing_only)

def intake_locked(discovery,root,identifiers,existing_only):
    data=json.loads(Path(discovery).read_text(encoding='utf-8'))
    if data.get('schemaVersion')!=1 or data.get('purpose')!='unreviewed candidate-media-pool':raise ValueError('invalid_discovery_manifest')
    selected=[item for item in data['items'] if item['id'] in identifiers]
    if not selected or len(selected)!=len(identifiers) or len(set(identifiers))!=len(identifiers) or len(selected)>100 or sum(item['original']['bytes'] for item in selected)>MAX_TOTAL:raise ValueError('selection_budget_or_missing_id')
    existing=root/'intake.json'
    if existing.exists():raise FileExistsError('Refusing to overwrite an intake manifest; use a new batch directory')
    for item in selected:
        item['local']=download(item,root,existing_only);print('Verified '+item['id']+' '+item['family'],flush=True)
        if not existing_only:time.sleep(2)
    result=dict(schemaVersion=1,purpose='unreviewed candidate-media-pool',discoverySha256=digest(discovery),createdUtc=datetime.now(timezone.utc).isoformat(),items=selected)
    exclusive_json(existing,result)
    print('Source pool is unreviewed. Content/license/group/split checks and independently authored labels are required before a dataset freeze.')
def main():
    parser=argparse.ArgumentParser(description=__doc__);commands=parser.add_subparsers(dest='command',required=True)
    discover_cmd=commands.add_parser('discover');discover_cmd.add_argument('--queries',type=Path,required=True);discover_cmd.add_argument('--output',type=Path,required=True)
    intake_cmd=commands.add_parser('intake');intake_cmd.add_argument('--discovery',type=Path,required=True);intake_cmd.add_argument('--output',type=Path,required=True);intake_cmd.add_argument('--ids',nargs='+',required=True)
    intake_cmd.add_argument('--existing-only',action='store_true',help='Verify and publish a selected subset of existing originals without any network access.')
    args=parser.parse_args()
    if args.command=='discover':discover(args.queries,args.output)
    else:intake(args.discovery,args.output,args.ids,args.existing_only)
if __name__=='__main__':main()
