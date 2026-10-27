const rooms=document.getElementById('rooms'),navigation=document.getElementById('navigation'),messages=document.getElementById('messages'),feed=document.getElementById('messageFeed'),status=document.getElementById('status'),input=document.getElementById('text'),sendButton=document.getElementById('send'),picker=document.getElementById('filePicker'),pendingBox=document.getElementById('pendingFiles'),notice=document.getElementById('notice');
let selected='',signature='',updating=false,sending=false,listCache=[],navSignature='',dragDepth=0,managing=false;
const drafts=new Map();
const draft=id=>{if(!drafts.has(id))drafts.set(id,{text:'',files:[]});return drafts.get(id)};
const sizeText=size=>size<1024?size+' B':size<1048576?(size/1024).toFixed(1)+' KB':(size/1048576).toFixed(1)+' MB';
const make=(tag,cls,text)=>{const el=document.createElement(tag);if(cls)el.className=cls;if(text!==undefined)el.textContent=text;return el};
function inform(text,error=false){notice.textContent=text;notice.hidden=!text;notice.classList.toggle('error',error)}
function autoHeight(){input.style.height='auto';input.style.height=Math.min(140,Math.max(43,input.scrollHeight))+'px'}
function colorFor(text){let hash=0;const value=String(text||'');for(let i=0;i<value.length;i++)hash=((hash*31)+value.charCodeAt(i))|0;return `hsl(${Math.abs(hash)%360}, 68%, 71%)`}
function roleName(name){const chip=make('span','role-name',name||'AI');chip.style.setProperty('--chip-color',colorFor(name));return chip}
function updateAvailability(){const active=!!selected;sendButton.disabled=!active||sending||(!input.value.trim()&&!draft(selected).files.length);document.getElementById('attach').disabled=!active||sending;if(input.disabled!==!active)input.disabled=!active;sendButton.querySelector('span').textContent=sending?'发送中':'发送';for(const id of ['newRoom','clearRoom','newRoomMobile','clearRoomMobile'])document.getElementById(id).disabled=!active||sending||managing}
function showDraft(){input.value=selected?draft(selected).text:'';autoHeight();renderPending();updateAvailability()}
function selectRoom(id){if(id===selected)return;if(selected)draft(selected).text=input.value;selected=id;signature='';rooms.value=id;feed.replaceChildren(make('div','empty-state','正在加载会话…'));inform('');showDraft();updateRoomHeader();renderRooms();refresh()}
function updateRoomHeader(){const room=listCache.find(c=>c.id===selected);document.getElementById('chatTitle').replaceChildren(room?roleName(room.title):document.createTextNode('选择一个会话'));document.getElementById('chatSubtitle').textContent=room?(room.kind==='group'?'群聊 · 与桌面同步':'私聊 · 与桌面同步'):'与桌面同步';document.getElementById('typingStatus').textContent=!room?'选择会话后开始聊天':room.busy?'对方正在回复…':'支持选择、拖放或粘贴图片和文件'}
function renderRooms(){const next=JSON.stringify([selected,listCache]);if(next===navSignature)return;navSignature=next;rooms.replaceChildren();navigation.replaceChildren(make('div','nav-heading','会话'));for(const c of listCache){rooms.append(new Option((c.kind==='group'?'群聊 · ':'')+c.title,c.id));const b=make('button',c.id===selected?'active':'');b.type='button';b.dataset.id=c.id;const avatar=make('span','room-avatar',c.kind==='group'?'群':Array.from(c.title||'聊')[0]);avatar.style.setProperty('--chip-color',colorFor(c.title));b.append(avatar);const info=make('span','room-info');info.append(make('strong','',c.title),make('small','',c.busy?'正在回复…':c.kind==='group'?'群聊会话':'私聊会话'));b.append(info);b.addEventListener('click',()=>selectRoom(c.id));navigation.append(b)}rooms.value=selected}
function fileIcon(name){return make('span','file-icon',(name.split('.').pop()||'FILE').slice(0,4).toUpperCase())}
function renderPending(){pendingBox.replaceChildren();const items=selected?draft(selected).files:[];pendingBox.hidden=!items.length;for(const item of items){const card=make('div','pending-file');if(item.preview){const img=make('img','pending-thumb');img.src=item.preview;img.alt='';card.append(img)}else card.append(fileIcon(item.file.name));const info=make('span','file-info');info.append(make('strong','',item.file.name),make('small','',item.state||sizeText(item.file.size)));const remove=make('button','remove-file','×');remove.type='button';remove.setAttribute('aria-label','移除 '+item.file.name);remove.disabled=sending;remove.addEventListener('click',()=>{const d=draft(selected);d.files=d.files.filter(f=>f!==item);if(item.preview)URL.revokeObjectURL(item.preview);renderPending();updateAvailability()});card.append(info,remove);pendingBox.append(card)}}
function addFiles(files){if(!selected||sending)return;const d=draft(selected),errors=[];for(const file of files){if(d.files.length>=8){errors.push('每条消息最多 8 个附件');break}if(file.size>20*1024*1024){errors.push(file.name+' 超过 20 MB');continue}d.files.push({file,preview:/^image\/(png|jpeg|gif|webp|bmp)$/.test(file.type)?URL.createObjectURL(file):'',uploadId:'',state:''})}inform(errors.join('；'),!!errors.length);renderPending();updateAvailability()}
function appendFiles(bubble,attachments){if(!attachments?.length)return;const box=make('div','message-files');for(const a of attachments){if(a.available===false){const expired=make('div','file-card');expired.append(fileIcon(a.name||''),make('span','', (a.name||'附件')+' · 临时文件已清理'));box.append(expired);continue}if(!a.url?.startsWith('/api/'))continue;const link=make('a',a.kind==='image'?'image-link':'file-card');link.href=a.url;link.target='_blank';link.rel='noopener';if(a.kind==='image'){const img=make('img','attachment-image');img.src=a.url;img.alt=a.name||'图片';img.loading='lazy';img.addEventListener('load',()=>{if(messages.scrollHeight-messages.scrollTop-messages.clientHeight<300)messages.scrollTop=messages.scrollHeight});link.append(img,make('span','image-caption',a.name||'图片'))}else{link.download=a.name||'附件';link.append(fileIcon(a.name||''));const info=make('span','file-info');info.append(make('strong','',a.name||'附件'),make('small','','点击下载'));link.append(info)}box.append(link)}bubble.append(box)}
function updateTurnHeader(turn,m,actor){let meta=turn.querySelector(':scope > .meta');if(!meta){meta=make('div','meta');turn.prepend(meta)}const name=m.role==='assistant'?(m.senderName||actor||'角色'):(actor||'角色');meta.replaceChildren(roleName(name));const date=new Date(m.createdAt);if(!Number.isNaN(date.getTime()))meta.append(make('time','',date.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})))}
function createEventDetail(message) {
  const system = message.activityKind === "system_event";
  const tool = !system && /工具/.test(message.activityTitle || "");
  const detail = document.createElement(tool ? "details" : "section");
  detail.className = "system-activity-detail event-entry " + (system ? "event-system" : "event-assistant") + (tool ? " event-tool" : "");
  if (tool) {
    detail.open = false;
    detail.addEventListener("toggle", () => {
      if (detail.isConnected && typeof requestResize === "function") requestResize();
    });
  }
  const header = document.createElement(tool ? "summary" : "div");
  header.className = "event-entry-header";
  const label = document.createElement("span");
  const actor = message.senderName || message.petName || (message.petId !== "system" ? message.petId : "") || "AI";
  label.textContent = system ? "系统事件" : actor + " · " + (message.activityTitle || "处理过程");
  if (!system) label.replaceChildren(roleName(actor), document.createTextNode(" · " + (message.activityTitle || "处理过程")));
  const time = document.createElement("span");
  time.className = "event-entry-time";
  const date = new Date(message.createdAt);
  time.textContent = Number.isNaN(date.getTime()) ? "" : date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  header.append(label, time);
  const content = document.createElement("div");
  content.className = "event-entry-content";
  content.textContent = message.text || "";
  detail.append(header, content);
  return detail;
}

function foldSilentSystemActivities(messages) {
  const result = [];
  for (let index = 0; index < messages.length;) {
    const first = messages[index];
    if (first.role !== "activity" || first.activityKind !== "system_event") {
      result.push(first); index++; continue;
    }
    let end = index;
    while (end < messages.length && messages[end].role === "activity") end++;
    let foldEnd = end;
    // A spoken reply keeps its own latest thought/tool sequence in its card.
    if (messages[end]?.role === "assistant") {
      for (let i = end - 1; i >= index; i--) {
        if (messages[i].activityKind === "system_event") { foldEnd = i; break; }
      }
    }
    if (foldEnd > index) {
      const activities = messages.slice(index, foldEnd);
      result.push({ ...first, foldedActivities: activities });
      index = foldEnd;
    } else {
      result.push(first); index++;
    }
  }
  return result;
}


function renderHistory(data,simpleMode){const next=JSON.stringify([selected,simpleMode,data]);if(signature===next)return;const bottom=signature===''||messages.scrollHeight-messages.scrollTop-messages.clientHeight<100;const oldDistance=messages.scrollHeight-messages.scrollTop;const open=new Set([...feed.querySelectorAll('details[open]')].map(d=>d.dataset.id));feed.replaceChildren();let process=null,turn=null,turnActor='',lastAssistant=false,systemGroup=null,systemCount=0,anchor='start';
for(const m of foldSilentSystemActivities(data).filter(m=>!simpleMode||m.role!=='activity')){const actor=m.petId&&m.petId!=='system'&&m.petId!=='__user__'?m.petId:'';
if(m.role==='activity'){if(m.activityKind==='system_event'){if(!systemGroup){systemGroup=make('details','system-group');systemGroup.dataset.id='system:'+m.id;systemGroup.append(make('summary'));feed.append(systemGroup);systemCount=0}for(const item of m.foldedActivities||[m]){if(item.activityKind==='system_event')systemCount++;systemGroup.append(createEventDetail(item))}systemGroup.querySelector('summary').textContent='系统主动事件 · '+systemCount+' 次';process=null;turn=null;turnActor='';lastAssistant=false;anchor=m.id||anchor;continue}
systemGroup=null;if(!turn||(turnActor&&actor&&turnActor!==actor)){turn=make('div','turn');feed.append(turn);turnActor=actor;process=null;updateTurnHeader(turn,m,turnActor)}if(!turnActor&&actor){turnActor=actor;updateTurnHeader(turn,m,turnActor)}if(!process){process=make('details','thought');process.dataset.id='process:'+selected+':'+anchor;process.open=open.has(process.dataset.id);process.append(make('summary','','思考与工具过程'));turn.append(process)}const row=make('details');row.dataset.id=m.id;row.open=open.has(m.id);row.append(make('summary','',m.activityTitle||'处理过程'),make('div','process-text',m.text||''));process.append(row);lastAssistant=false;continue}
process=null;systemGroup=null;anchor=m.id||anchor;const bubble=make('div','bubble '+(m.role==='user'?'user':''));if(m.role!=='assistant'){const meta=make('div','meta');meta.append(roleName(m.role==='user'?'我':m.senderName||'系统'));const date=new Date(m.createdAt);if(!Number.isNaN(date.getTime()))meta.append(make('time','',date.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})));bubble.append(meta)}
if(!simpleMode&&m.reasoning){const details=make('details','thought');details.dataset.id=m.id;details.open=open.has(m.id);details.append(make('summary','','思考过程'),make('div','process-text',m.reasoning));bubble.append(details)}if(m.text&&!(m.attachments?.length&&m.text==='[附件]'))bubble.append(make('div','message-text',m.text));appendFiles(bubble,m.attachments);if(m.deliveryState==='sending'||m.deliveryState==='failed')bubble.append(make('div','delivery-label '+(m.deliveryState==='failed'?'failed':''),m.deliveryState==='sending'?'发送中…':'未送达'));
if(m.role==='assistant'){if(!turn||lastAssistant||(turnActor&&actor&&turnActor!==actor)){turn=make('div','turn');feed.append(turn);turnActor=actor}if(!turnActor)turnActor=actor;updateTurnHeader(turn,m,turnActor);turn.append(bubble);lastAssistant=true}else{feed.append(bubble);turn=null;turnActor='';lastAssistant=false}}
if(!feed.children.length)feed.append(make('div','empty-state','还没有消息\n发送一句话，开始聊天吧。'));signature=next;messages.scrollTop=bottom?messages.scrollHeight:Math.max(0,messages.scrollHeight-oldDistance)}
async function refresh(){if(updating)return;updating=true;try{const r=await fetch('/api/conversations',{cache:'no-store'});if(!r.ok)throw Error();listCache=await r.json();window.QuickChatThemes?.apply(listCache[0]?.themeStyle);if(!listCache.some(c=>c.id===selected)){if(selected)draft(selected).text=input.value;selected=listCache[0]?.id||'';signature='';showDraft()}renderRooms();updateRoomHeader();if(selected){const requested=selected,h=await fetch('/api/history?id='+encodeURIComponent(requested),{cache:'no-store'});if(!h.ok)throw Error();const data=await h.json();if(requested!==selected)return;renderHistory(data,listCache.find(c=>c.id===selected)?.simpleMode===true)}else{feed.replaceChildren(make('div','empty-state','桌面端还没有可用会话'));signature=''}status.className='connection online';status.querySelector('span').textContent='已连接';updateAvailability()}catch{status.className='connection offline';status.querySelector('span').textContent='连接中断 · 重试中'}finally{updating=false}}
async function send(){const id=selected;if(!id||sending)return;const d=draft(id),text=input.value.trim(),files=d.files.slice();if(!text&&!files.length)return;sending=true;d.text='';input.value='';autoHeight();inform('');updateAvailability();renderPending();try{for(let i=0;i<files.length;i++){const item=files[i];if(item.uploadId)continue;item.state='上传中 · '+(i+1)+'/'+files.length;if(selected===id)renderPending();const form=new FormData();form.append('file',item.file,item.file.name);const upload=await fetch('/api/upload?conversationId='+encodeURIComponent(id),{method:'POST',body:form});if(!upload.ok){const result=await upload.json().catch(()=>({}));throw Error(result.error||'附件上传失败')}const result=await upload.json();item.uploadId=result.id;item.state='已上传 · '+sizeText(item.file.size)}const r=await fetch('/api/send',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({conversationId:id,text,attachmentIds:files.map(f=>f.uploadId)})});if(!r.ok)throw Error('消息发送失败');d.files=d.files.filter(f=>!files.includes(f));for(const f of files)if(f.preview)URL.revokeObjectURL(f.preview);inform('');await refresh()}catch(e){d.text=text+(d.text?'\n'+d.text:'');for(const f of files)f.state=f.uploadId?'已上传 · 可重试':'';if(selected===id){input.value=d.text;autoHeight()}inform(e.message+'，内容已保留，可重试。',true)}finally{sending=false;renderPending();updateAvailability();if(selected===id)input.focus()}}
rooms.addEventListener('change',()=>selectRoom(rooms.value));document.getElementById('attach').addEventListener('click',()=>picker.click());picker.addEventListener('change',()=>{addFiles(Array.from(picker.files));picker.value=''});sendButton.addEventListener('click',send);input.addEventListener('input',()=>{if(selected)draft(selected).text=input.value;autoHeight();updateAvailability()});input.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey&&!e.isComposing){e.preventDefault();send()}});input.addEventListener('paste',e=>{const files=Array.from(e.clipboardData?.files||[]);if(files.length){e.preventDefault();addFiles(files)}});
window.addEventListener('dragenter',e=>{if(Array.from(e.dataTransfer?.types||[]).includes('Files')){e.preventDefault();dragDepth++;document.body.classList.add('file-drag')}});window.addEventListener('dragover',e=>{if(Array.from(e.dataTransfer?.types||[]).includes('Files'))e.preventDefault()});window.addEventListener('dragleave',()=>{if(--dragDepth<=0){dragDepth=0;document.body.classList.remove('file-drag')}});window.addEventListener('drop',e=>{if(e.dataTransfer?.files.length){e.preventDefault();addFiles(Array.from(e.dataTransfer.files))}dragDepth=0;document.body.classList.remove('file-drag')});
showDraft();refresh();setInterval(refresh,1500);

async function newConversation(){if(!selected||managing)return;const source=selected;managing=true;updateAvailability();try{const response=await fetch('/api/conversation/create',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({conversationId:source})});if(!response.ok)throw Error('新建会话失败');const result=await response.json();await refresh();selectRoom(result.conversationId);inform('已新建独立会话，原会话记录已保留。')}catch(e){inform(e.message,true)}finally{managing=false;updateAvailability()}}
let clearingId='';
function requestClear(){if(!selected||managing)return;clearingId=selected;const room=listCache.find(c=>c.id===selected);document.getElementById('clearDescription').textContent='确定清空「'+(room?.title||'当前会话')+'」的聊天记录吗？';document.getElementById('purgeOriginal').checked=room?.clearIncludesOriginal!==false;document.getElementById('clearError').hidden=true;document.getElementById('clearDialog').showModal()}
async function confirmClear(){if(!clearingId||managing)return;const id=clearingId,button=document.getElementById('confirmClear');managing=true;button.disabled=true;updateAvailability();try{const response=await fetch('/api/conversation/clear',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({conversationId:id,purge:document.getElementById('purgeOriginal').checked})});if(!response.ok)throw Error('清空会话失败，请重试');document.getElementById('clearDialog').close();signature='';await refresh();inform('会话记录已清空。')}catch(e){const error=document.getElementById('clearError');error.textContent=e.message;error.hidden=false}finally{managing=false;button.disabled=false;updateAvailability()}}
for(const id of ['newRoom','newRoomMobile'])document.getElementById(id).addEventListener('click',newConversation);
for(const id of ['clearRoom','clearRoomMobile'])document.getElementById(id).addEventListener('click',requestClear);
document.getElementById('cancelClear').addEventListener('click',()=>document.getElementById('clearDialog').close());document.getElementById('confirmClear').addEventListener('click',confirmClear);
