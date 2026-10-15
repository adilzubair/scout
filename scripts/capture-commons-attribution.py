"""Capture current publisher evidence for existing Commons candidates; never approve rights or labels."""
import argparse
from datetime import datetime,timezone
from html.parser import HTMLParser
import importlib.util
import json
import os
from pathlib import Path
import re
import time
import urllib.parse

spec=importlib.util.spec_from_file_location('media_intake',Path(__file__).with_name('multimodal-media-intake.py'))
media=importlib.util.module_from_spec(spec);spec.loader.exec_module(media)
PURPOSE='Current Commons attribution evidence; not license clearance or label approval'

def read_json(path,limit=4*1024**2):
    if not 0<path.stat().st_size<=limit:raise ValueError('metadata_size_budget')
    return json.loads(path.read_text(encoding='utf-8-sig'))

class Links(HTMLParser):
    def __init__(self):super().__init__();self.urls=[]
    def handle_starttag(self,tag,attrs):
        if tag!='a':return
        href=dict(attrs).get('href','')
        if href.startswith('//'):href='https:'+href
        elif href.startswith('/'):href='https://commons.wikimedia.org'+href
        parsed=urllib.parse.urlsplit(href)
        if parsed.scheme in ('https','http') and parsed.hostname and not parsed.username and not parsed.password and len(href)<=2000 and href not in self.urls:
            if len(self.urls)<32:self.urls.append(href)

def summarize(item,page):
    reasons=[];info=((page or {}).get('imageinfo') or [{}])[0];metadata=info.get('extmetadata') or {}
    def field(name):return metadata.get(name,{}).get('value','')
    links=Links();links.feed(field('Artist'))
    if not info:reasons.append('publisher_page_or_file_unavailable')
    if info.get('sha1')!=item['original']['sha1'] or info.get('size')!=item['original']['bytes']:reasons.append('publisher_original_changed')
    current_license=media.text(field('LicenseShortName'))
    if current_license!=item['license']['name']:reasons.append('license_label_differs')
    artist=media.text(field('Artist'))
    if not artist or artist!=item['license'].get('artist'):reasons.append('artist_missing_or_differs')
    if media.text(field('Restrictions')):reasons.append('restrictions_present')
    revision=((page or {}).get('revisions') or [{}])[0]
    if type(revision.get('revid')) is not int or revision['revid']<=0:reasons.append('missing_page_revision')
    wikitext=revision.get('slots',{}).get('main',{}).get('*','')
    return dict(status='metadata_match' if not reasons else 'requires_review',reasons=reasons,
        currentRevision=revision.get('revid'),originalDiscoveryRevision=item.get('revision'),
        currentLicense=current_license,artist=artist,artistUrls=links.urls,
        creditHtml=field('Credit'),restrictions=media.text(field('Restrictions')),licenseUrl=media.text(field('LicenseUrl')),
        sourceWikitextPresent=bool(wikitext),approvedForHoldout=False)

def selected(intake):
    data=read_json(intake)
    if data.get('schemaVersion')!=1 or data.get('purpose')!='unreviewed candidate-media-pool' or not 0<len(data.get('items',[]))<=100:
        raise ValueError('invalid_commons_intake')
    items=data['items'];ids=set();paths=set();root=intake.parent;total=0
    for item in items:
        identity=item['id'];local=item['local'];original=item['original']
        if not re.fullmatch(r'commons-[1-9][0-9]{0,19}',identity) or identity in ids or type(item['pageId']) is not int or item['pageId']!=int(identity[8:]):raise ValueError('invalid_commons_identity')
        ids.add(identity)
        if local['file'] in paths or not re.fullmatch(r'sources/f-[a-f0-9]{12}\.[a-z0-9]{1,5}',local['file']) or not re.fullmatch('[a-f0-9]{64}',local['sha256']):raise ValueError('invalid_local_source')
        paths.add(local['file'])
        path=media.owned(root,root/local['file'])
        if type(local['bytes']) is not int or not 0<local['bytes']<=media.MAX_FILE or path.stat().st_size!=local['bytes'] or local['bytes']!=original['bytes']:
            raise ValueError('source_size_changed')
        total+=local['bytes']
        if total>media.MAX_TOTAL:raise ValueError('source_batch_budget')
        if media.digest(path)!=local['sha256'] or media.digest(path,'sha1')!=original['sha1']:raise ValueError('source_checksum_changed')
    return items

def capture(intake,output,existing_only=False):
    intake=Path(os.path.abspath(intake));media.owned(intake.parent,intake)
    output=Path(os.path.abspath(output));media.owned(output,output/'attribution.json');output.mkdir(parents=True,exist_ok=True)
    with media.owner_lock(output,'.commons-attribution.lock'):
        items=selected(intake);intake_hash=media.digest(intake);summary=output/'attribution.json'
        summary_exists=summary.exists();prior=read_json(summary) if summary_exists else None
        if summary_exists:
            if not existing_only:raise FileExistsError('refusing_to_replace_attribution_evidence')
            if (not isinstance(prior,dict) or prior.get('schemaVersion')!=1 or prior.get('purpose')!=PURPOSE or prior.get('intakeSha256')!=intake_hash or prior.get('sourceCount')!=len(items) or len(prior.get('items',[]))!=len(items) or
                prior.get('approvedForHoldout') is not False or prior.get('labelsApproved') is not False or prior.get('split') is not None):raise ValueError('attribution_summary_changed')
            expected={row['id']:row for row in prior['items']}
            if set(expected)!={item['id'] for item in items}:raise ValueError('attribution_summary_changed')
        else:expected={}
        missing=[item for item in items if not (output/(item['id']+'.json')).exists()]
        if missing and existing_only:raise FileNotFoundError('missing_offline_attribution')
        for start in range(0,len(missing),10):
            if start:time.sleep(2)
            batch=missing[start:start+10];allowed={item['pageId'] for item in batch};pages={}
            fetched=media.fetch_json(dict(pageids='|'.join(str(item['pageId']) for item in batch),prop='imageinfo|revisions',
                iiprop='url|size|sha1|mime|timestamp|extmetadata',rvprop='ids|timestamp|content',rvslots='main'))
            for page in fetched:
                identity=page.get('pageid')
                if type(identity) is not int or identity not in allowed or identity in pages:raise ValueError('unexpected_publisher_identity')
                pages[identity]=page
            for item in batch:
                path=media.owned(output,output/(item['id']+'.json'))
                snapshot=dict(schemaVersion=1,id=item['id'],intakeSha256=intake_hash,sourceSha256=item['local']['sha256'],
                    capturedUtc=datetime.now(timezone.utc).isoformat(),publisherApi=media.API,page=pages.get(item['pageId']),approvedForHoldout=False)
                media.exclusive_json(path,snapshot)
        rows=[]
        for item in items:
            path=media.owned(output,output/(item['id']+'.json'))
            snapshot=read_json(path)
            if snapshot.get('schemaVersion')!=1 or snapshot.get('id')!=item['id'] or snapshot.get('intakeSha256')!=intake_hash or snapshot.get('sourceSha256')!=item['local']['sha256'] or snapshot.get('approvedForHoldout') is not False or snapshot.get('publisherApi')!=media.API:
                raise ValueError('attribution_snapshot_changed')
            page=snapshot.get('page')
            if page is not None and page.get('pageid')!=item['pageId']:raise ValueError('attribution_snapshot_changed')
            row=dict(id=item['id'],sourceSha256=item['local']['sha256'],snapshot=path.name,snapshotSha256=media.digest(path),evidence=summarize(item,page))
            if summary_exists and row!=expected[item['id']]:raise ValueError('attribution_snapshot_or_summary_changed')
            rows.append(row)
        if media.digest(intake)!=intake_hash:raise ValueError('intake_changed_during_capture')
        selected(intake) # Detect source mutation during the publisher request or offline replay.
        if not summary_exists:media.exclusive_json(summary,dict(schemaVersion=1,purpose=PURPOSE,intakeSha256=intake_hash,
            capturedUtc=datetime.now(timezone.utc).isoformat(),sourceCount=len(rows),items=rows,approvedForHoldout=False,labelsApproved=False,split=None))
        print(f"Verified {len(rows)} publisher snapshots; {sum(row['evidence']['status']=='metadata_match' for row in rows)} metadata matches. Rights/content/group approval remains open.")

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--intake',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--existing-only',action='store_true',help='Verify pinned snapshots and summary without network access or rewriting evidence.')
    args=parser.parse_args();capture(args.intake,args.output,args.existing_only)
