using System;
using System.IO;
using System.Text.Json;

namespace Truedat
{
    /// <summary>
    /// Serialises a PreviewPlan to preview.json. Two things about this file are contract,
    /// not preference:
    ///
    /// 1. preview.json IS the review-surface manifest MBXHub serves via GET /review/{id} —
    ///    their GetAsset route serves only .html and no route serves an arbitrary sibling
    ///    JSON, so the plan payload has to ride inside the manifest envelope rather than
    ///    living beside it.
    /// 2. An unestimable ETA is OMITTED, never written as the -1 sentinel. A consumer that
    ///    reads -1 as a number would render "-1 seconds"; absent is unambiguous.
    /// </summary>
    internal static class PreviewWriter
    {
        public const string FileName = "preview.json";

        /// <summary>Review-folder destination, falling back beside the moods file.</summary>
        public static string ResolveDest(string moodsDir)
        {
            var rd = Program.ResolveReviewDir(moodsDir);
            if (rd != null) return Path.Combine(rd, FileName);
            Console.WriteLine("  (no MusicBee/MBXHub instance found — writing preview next to moods; pass --preview <path> to override)");
            return Path.Combine(moodsDir, "mbxmoods-preview.json");
        }

        /// <summary>
        /// True when <paramref name="path"/> already holds something a preview must never
        /// truncate: an .xml (the iTunes library is --preview's INPUT), or a JSON object with
        /// a top-level "tracks" property (a mood catalog). --preview's headline contract is
        /// "writes no mbxmoods.json", so overwriting one is the single worst thing this mode
        /// can do; and because every adjacent read-only mode takes its moods path
        /// positionally, naming the catalog after --preview is a plausible mistake rather
        /// than a contrived one. Tolerant-read, matching the rest of the codebase: a file
        /// that does not parse as JSON is not a catalog and may be overwritten.
        /// </summary>
        public static bool IsProtectedTarget(string path, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(path)) return false;
            if (!File.Exists(path)) return false;
            if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                reason = "it is an XML file (the iTunes library is --preview's input, not its output)";
                return true;
            }
            if (HasTopLevelTracksObject(path))
            {
                reason = "it is a mood catalog (top-level \"tracks\" property)";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Bounded head scan for a top-level "tracks" property. Deliberately a fixed window
        /// rather than a JsonDocument parse of the whole file: a real mbxmoods.json runs to
        /// hundreds of MB, and it writes "tracks" as its fourth top-level property (after
        /// version / generatedAt / trackCount), so the window always reaches the answer
        /// while a full parse of the catalog would not be free. Any parse failure means
        /// "not a catalog" — the guard's job is to recognise catalogs, not to validate JSON.
        /// </summary>
        static bool HasTopLevelTracksObject(string path)
        {
            try
            {
                byte[] head;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    head = new byte[(int)Math.Min(fs.Length, 256L * 1024)];
                    int got = 0;
                    while (got < head.Length)
                    {
                        int n = fs.Read(head, got, head.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < head.Length) Array.Resize(ref head, got);
                }
                int start = 0;
                if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) start = 3;
                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(head, start, head.Length - start),
                                                isFinalBlock: false, state: default(JsonReaderState));
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                    {
                        if (reader.ValueTextEquals("tracks")) return true;
                        continue;
                    }
                    if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                    {
                        // A value bigger than the window means we cannot see the rest of the
                        // top level; stop rather than guess.
                        if (!reader.TrySkip()) return false;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>Guarded write: refuses (and writes nothing) when the destination is
        /// something <see cref="IsProtectedTarget"/> recognises, printing which file and why.
        /// Callers turn a false return into exit 1. <paramref name="reviewHtmlName"/> is passed
        /// through so the manifest points at the co-emitted page only when a caller commits to
        /// emitting it.</summary>
        public static bool TryWritePreviewJson(string path, PreviewPlan plan, string? reviewHtmlName = null)
        {
            string why;
            if (IsProtectedTarget(path, out why))
            {
                Console.Error.WriteLine($"Error: refusing to overwrite {path} — {why}.");
                Console.Error.WriteLine("  --preview writes only a preview manifest. Name a preview file, or omit the path to use the default destination.");
                return false;
            }
            WritePreviewJson(path, plan, reviewHtmlName);
            return true;
        }

        public static void WritePreviewJson(string path, PreviewPlan plan, string? reviewHtmlName = null)
        {
            // ResolveDest's own targets (the MBXHub review folder, or beside the moods
            // file) already exist by construction, but an explicit --preview <path> can
            // name a directory that doesn't — mirror the tolerant-create-first pattern
            // used elsewhere for an explicit output path rather than letting FileStream
            // surface a raw DirectoryNotFoundException.
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            var opts = new JsonWriterOptions { Indented = true };
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new Utf8JsonWriter(fs, opts))
                WriteManifest(w, plan, reviewHtmlName);
        }

        /// <summary>Single source of truth for the manifest object — written to preview.json
        /// AND embedded in the review page, so the two can never disagree. When
        /// <paramref name="reviewHtmlName"/> is set, source.reviewHtml names the co-emitted
        /// page; otherwise it is omitted (never point at a file that does not exist).</summary>
        static void WriteManifest(Utf8JsonWriter w, PreviewPlan plan, string? reviewHtmlName)
        {
            {
                w.WriteStartObject();

                // --- review-surface envelope ---
                w.WriteString("id", "preview");
                w.WriteString("kind", "preview");
                w.WriteNumber("schemaVersion", 1);
                w.WriteString("title", "Scan preview");
                w.WriteString("generated", DateTime.UtcNow.ToString("o"));
                w.WriteStartObject("source");
                w.WriteString("xmlPath", plan.XmlPath);
                w.WriteString("moodsPath", plan.MoodsPath);
                w.WriteString("exclusionsPath", plan.ExclusionsPath);
                if (!string.IsNullOrEmpty(reviewHtmlName)) w.WriteString("reviewHtml", reviewHtmlName);
                w.WriteEndObject();

                w.WriteStartObject("limits");
                w.WriteNumber("maxDurationSecs", plan.Limits.MaxDurationSecs);
                w.WriteString("maxDurationSource", plan.Limits.MaxDurationSource);
                w.WriteNumber("longTrackSecs", plan.Limits.LongTrackSecs);
                w.WriteString("extractorNote", plan.Limits.ExtractorNote);
                w.WriteEndObject();

                w.WriteStartObject("counts");
                w.WriteNumber("libraryTotal", plan.Counts.LibraryTotal);
                w.WriteNumber("analyzed", plan.Counts.Analyzed);
                w.WriteNumber("excluded", plan.Counts.Excluded);
                w.WriteNumber("awaitingReview", plan.Counts.AwaitingReview);
                w.WriteEndObject();

                w.WriteStartObject("estimate");
                w.WriteNumber("newTracks", plan.Estimate.NewTracks);
                w.WriteNumber("newBytes", plan.Estimate.NewBytes);
                w.WriteNumber("cachedTracks", plan.Estimate.CachedTracks);
                if (plan.Estimate.EtaSecs >= 0) w.WriteNumber("etaSecs", Math.Round(plan.Estimate.EtaSecs, 1));
                w.WriteString("etaBasis", plan.Estimate.EtaBasis);
                w.WriteEndObject();

                w.WriteStartArray("autoSkip");
                foreach (var b in plan.AutoSkip)
                {
                    w.WriteStartObject();
                    w.WriteString("class", b.Class);
                    w.WriteNumber("count", b.Count);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("rules");
                foreach (var r in plan.Rules)
                {
                    w.WriteStartObject();
                    w.WriteString("rule", r.Rule);
                    w.WriteString("action", r.Action);
                    w.WriteNumber("matchCount", r.MatchCount);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("genres");
                foreach (var g in plan.Genres)
                {
                    w.WriteStartObject();
                    w.WriteString("name", g.Name);
                    w.WriteNumber("tracks", g.Tracks);
                    w.WriteNumber("totalSecs", g.TotalSecs);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteNumber("reviewTotal", plan.ReviewTotal);
                w.WriteBoolean("reviewTruncated", plan.ReviewTruncated);
                // Declared rather than left for the page to infer, so client-side paging
                // slices a list whose order it did not have to guess at.
                w.WriteString("reviewSort", "durationSecs-desc");
                w.WriteNumber("sniffedCount", plan.SniffedCount);

                w.WriteStartArray("review");
                foreach (var c in plan.Review)
                {
                    w.WriteStartObject();
                    w.WriteString("path", c.Path);
                    w.WriteString("artist", c.Artist);
                    w.WriteString("title", c.Title);
                    w.WriteString("album", c.Album);
                    w.WriteString("genre", c.Genre);
                    w.WriteString("codec", c.Codec);
                    w.WriteNumber("durationSecs", c.DurationSecs);
                    w.WriteString("state", c.State);
                    w.WriteBoolean("overLimit", c.OverLimit);
                    w.WriteString("currentDecision", c.CurrentDecision);
                    if (c.SpeechLikely != null) w.WriteString("speechLikely", c.SpeechLikely);
                    w.WriteStartArray("reasons");
                    foreach (var reason in c.Reasons) w.WriteStringValue(reason);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteEndObject();
            }
        }

        public const string PreviewHtmlFileName = "mbxmoods-preview.html";

        /// <summary>Co-emitted review page. Serialises the SAME manifest WritePreviewJson writes
        /// (via WriteManifest, so page and json can never disagree) and splices it into the
        /// self-contained template at __DATA__. UTF-8 no-BOM, matching WriteDuplicatesHtml.</summary>
        public static void WritePreviewHtml(string htmlPath, PreviewPlan plan)
        {
            string dataJson;
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                    WriteManifest(w, plan, PreviewHtmlFileName);
                dataJson = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            var dir = Path.GetDirectoryName(Path.GetFullPath(htmlPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            File.WriteAllText(htmlPath, PreviewHtmlTemplate.Replace("__DATA__", dataJson),
                              new System.Text.UTF8Encoding(false));
        }

        // Self-contained review page. Single-quote HTML/JS so this C# verbatim literal needs
        // no doubled quotes except inside esc(). __DATA__ becomes the manifest JSON. Renders
        // the preview manifest and emits an exclusion DECISIONS DELTA (add/remove) — it never
        // writes the exclusion file (that is `truedat --apply-exclusions`). Visual language is
        // copied from DuplicatesHtmlTemplate so both truedat review surfaces read as one tool.
        const string PreviewHtmlTemplate = @"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>truedat — scan preview</title>
<style>
  :root{color-scheme:dark light}
  body{font-family:system-ui,'Segoe UI',sans-serif;margin:0;background:#141414;color:#e8e8e8}
  header{position:sticky;top:0;z-index:5;background:#1c1c1c;border-bottom:1px solid #333;padding:10px 16px;display:flex;gap:14px;align-items:center;flex-wrap:wrap}
  header h1{font-size:15px;margin:0;font-weight:600}
  .counts{font-size:13px;color:#9cf}
  .badge{background:#333;border-radius:4px;padding:2px 7px;font-size:12px}
  button{background:#2e7d46;color:#fff;border:0;padding:8px 14px;border-radius:6px;cursor:pointer;font-size:14px}
  button:hover{background:#37984f}
  button:disabled{background:#333;color:#777;cursor:default}
  button.sec{background:#3a3a3a}
  button.sec:hover{background:#4a4a4a}
  .tools{margin-left:auto;display:flex;gap:10px;align-items:center;font-size:13px;color:#bbb}
  main{padding:16px;max-width:1200px;margin:0 auto}
  .strip{background:#191919;border:1px solid #333;border-radius:8px;padding:10px 14px;margin-bottom:14px;font-size:13px;line-height:1.7;color:#cfcfcf}
  .strip b{color:#e8e8e8}
  .strip .k{color:#888;text-transform:uppercase;letter-spacing:.04em;font-size:11px;margin-right:6px}
  .strip .ci{color:#666;cursor:help}
  .empty{color:#888;font-size:14px;padding:30px;text-align:center}
  .sec-h{color:#888;font-size:11px;text-transform:uppercase;letter-spacing:.04em;margin:14px 0 6px}
  .chips{display:flex;flex-wrap:wrap;gap:8px}
  .chip{background:#242424;border:1px solid #3a3a3a;border-radius:14px;padding:4px 11px;font-size:13px;color:#ddd;cursor:pointer;display:flex;gap:6px;align-items:center}
  .chip:hover{border-color:#4a4a4a}
  .chip .n{color:#888;font-size:12px}
  .chip.excl{background:#3a2020;border-color:#7a3030;color:#f2a0a0;text-decoration:line-through}
  .rules{display:flex;flex-direction:column;gap:4px;font-size:13px}
  .rule{display:flex;gap:10px;align-items:center;color:#cfcfcf}
  .rule .rn{color:#9cf}
  .rule .stale{color:#e0a04a}
  .rule .n{color:#888;font-size:12px}
  .rule.pending-rm{opacity:.5;text-decoration:line-through}
  .rule button{padding:2px 8px;font-size:12px}
  .slider{display:flex;gap:10px;align-items:center;font-size:13px;color:#cfcfcf}
  .slider input[type=range]{width:220px}
  .banner{background:#1e2230;border:1px solid #33507a;border-radius:6px;padding:8px 12px;margin-bottom:12px;font-size:13px;color:#cfe;display:flex;gap:10px;align-items:center}
  .banner.warn{background:#2e2418;border-color:#7a5a2a;color:#f2c48a}
  .banner code{background:#0d0d0d;padding:1px 6px;border-radius:4px;color:#9cf}
  .trunc{color:#e0a04a;font-size:13px;margin:10px 0}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{padding:6px 10px;text-align:left;border-top:1px solid #232323;vertical-align:top}
  th{color:#888;font-weight:500;font-size:11px;text-transform:uppercase;letter-spacing:.04em}
  td.num,th.num{text-align:right;color:#bbb}
  tr.excl td{background:#2a1a1a}
  tr.incl td{background:#17281b}
  .path{word-break:break-all;color:#dcdcdc}
  .flink{color:#9cf;text-decoration:none}
  .flink:hover{text-decoration:underline}
  .rsn{display:inline-block;background:#333;border-radius:4px;padding:1px 6px;margin:1px;font-size:11px;color:#cbb}
  .rsn.long{background:#3a3020;color:#e0c48a}
  .rsn.over{background:#5a2020;color:#f2a0a0}
  .rsn.mark{background:#3a2050;color:#c9a0f2}
  .rsn.speech{background:#20405a;color:#a0d0f2}
  .rsn.excl{background:#5a3a1e;color:#f2c48a}
  .dec button{padding:2px 8px;font-size:12px;margin-right:3px}
  .dec button.on-x{background:#7a3030}
  .dec button.on-i{background:#2e7d46}
  .fexcl{margin-top:3px;font-size:11px;color:#888}
  .fexcl select{background:#242424;color:#ddd;border:1px solid #3a3a3a;border-radius:4px;font-size:11px}
</style>
</head>
<body>
<header>
  <h1>Scan preview</h1>
  <span class='counts' id='counts'></span>
  <button id='save' disabled>Save decisions</button>
  <button id='rescan' class='sec'>Rescan</button>
  <div class='tools'><span id='host'></span><button class='sec' id='reset'>reset</button></div>
</header>
<main>
  <div class='banner' id='banner' style='display:none'></div>
  <div class='strip' id='summary'></div>
  <div id='bulk'></div>
  <div id='body'></div>
</main>
<script id='data' type='application/json'>__DATA__</script>
<script>
const D=JSON.parse(document.getElementById('data').textContent);
const OFFLINE=location.protocol==='file:';
function esc(s){return(s==null?'':''+s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;');}
function bytes(n){if(!n)return '0 B';const u=['B','KB','MB','GB','TB'];let i=0,x=n;while(x>=1024&&i<4){x/=1024;i++;}return x.toFixed(i?1:0)+' '+u[i];}
function hms(s){if(s==null||s<0)return '—';s=Math.round(s);const h=Math.floor(s/3600),m=Math.floor(s%3600/60);return h?h+'h'+String(m).padStart(2,'0')+'m':(m?m+'m'+String(s%60).padStart(2,'0')+'s':s+'s');}
function num(n){return(n==null?0:n).toLocaleString();}
function folderOf(p){const i=Math.max(p.lastIndexOf('\\'),p.lastIndexOf('/'));return i<0?p:p.slice(0,i);}
function folderUrl(p){return 'file:///'+encodeURI(p.replace(/\\/g,'/'));}
// Ancestor fragments for a path, leaf-first: \Leaf\** , \Parent\** , ... so the operator
// names the subtree to exclude rather than the page guessing a level.
function ancestors(p){const parts=folderOf(p).split(/[\\/]/).filter(Boolean);const out=[];for(let i=parts.length-1;i>=1;i--)out.push('\\'+parts[i]+'\\**');return out;}

// ---- rule identity + delta -------------------------------------------------
// desired = the rule set the operator wants after this session; orig = what preview.json
// already reflects. Save emits only the difference. Identity mirrors the C# side closely
// enough to avoid a spurious duplicate add; the authoritative merge (dedupe, note/sha) is
// `truedat --apply-exclusions`.
function ruleTarget(r){return r.pattern!=null?r.pattern:(r.value!=null?r.value:(r.path!=null?r.path:''));}
function normTarget(kind,t){t=''+t;return kind==='genre'?t.trim().toLowerCase():t.replace(/\//g,'\\').replace(/\\+$/,'').toLowerCase();}
function idOf(r){return r.kind+'|'+r.action+'|'+normTarget(r.kind,ruleTarget(r));}
function parseRuleStr(s,action){const i=s.indexOf('=');const kind=s.slice(0,i),val=s.slice(i+1);if(kind==='folder')return{kind,action,pattern:val};if(kind==='genre')return{kind,action,value:val};return{kind,action,path:val};}
const orig=new Map(),byId=new Map();
(D.rules||[]).forEach(r=>{const ro=parseRuleStr(r.rule,r.action);const id=idOf(ro);orig.set(id,ro);byId.set(id,ro);});
const SKEY='truedat-preview:'+((D.source&&D.source.moodsPath)||'')+':'+(D.id||'');
let desired=new Set(orig.keys());
try{const saved=JSON.parse(localStorage.getItem(SKEY+':rules')||'{}');Object.keys(saved).forEach(id=>{if(!byId.has(id))byId.set(id,saved[id]);});}catch(e){}
try{const s=JSON.parse(localStorage.getItem(SKEY)||'null');if(Array.isArray(s))desired=new Set(s);}catch(e){}
function persist(){try{localStorage.setItem(SKEY,JSON.stringify([...desired]));const m={};byId.forEach((v,k)=>m[k]=v);localStorage.setItem(SKEY+':rules',JSON.stringify(m));}catch(e){}}
function desiredHas(rule){return desired.has(idOf(rule));}
function toggleRule(rule){const id=idOf(rule);byId.set(id,rule);if(desired.has(id))desired.delete(id);else desired.add(id);persist();refresh();}
// exclude/include on the same target are different identities; selecting one clears the
// other so a target never emits both.
function setExclusive(rule){const id=idOf(rule);byId.set(id,rule);const anti=idOf({kind:rule.kind,action:rule.action==='exclude'?'include':'exclude',pattern:rule.pattern,value:rule.value,path:rule.path});desired.delete(anti);if(desired.has(id))desired.delete(id);else desired.add(id);persist();refresh();}
function buildDelta(){const add=[],remove=[];desired.forEach(id=>{if(!orig.has(id)&&byId.has(id))add.push(byId.get(id));});orig.forEach((r,id)=>{if(!desired.has(id))remove.push(r);});return{schemaVersion:1,kind:'exclusion-decisions',generatedBy:'mbxmoods-preview.html',add,remove};}
function dirty(){const d=buildDelta();return d.add.length+d.remove.length>0;}

// ---- host adapter ----------------------------------------------------------
function download(name,text){const b=new Blob([text],{type:'application/json'});const u=URL.createObjectURL(b);const a=document.createElement('a');a.href=u;a.download=name;document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(u),1000);}
function banner(msg,ok){const b=document.getElementById('banner');b.className='banner'+(ok?'':' warn');b.innerHTML=esc(msg).replace(/`([^`]+)`/g,'<code>$1</code>');b.style.display='';}
const offlineHost={mode:'offline',
  load(){console.log('[preview] offline: using embedded manifest');return Promise.resolve(D);},
  save(delta){console.log('[preview] offline save: downloading delta',delta);download('preview-decisions.json',JSON.stringify(delta,null,2));banner('Delta downloaded. Apply it with:  `truedat --apply-exclusions preview-decisions.json`',true);return Promise.resolve({offline:true});},
  rescan(){banner('Offline: re-run  `truedat --preview`  to refresh this page.',false);return Promise.resolve();}};
// Served host: the MBXHub review-service contract (TOOL-LIFECYCLE-SERVICE.md §5-7).
//   load  -> GET  /review/manifest/{id}   (LIVE)
//   save  -> POST /review/decisions/{id}  (PLANNED) — response body IS apply-result.json
//            {ok,added,removed,alreadyPresent,notFound,changed,backupPath,error}; the POST
//            also re-runs --preview, so a follow-up load() picks up fresh counts.
//   rescan-> no standalone trigger yet (planned); re-load to pull the latest manifest.
// Every path ALWAYS degrades to embedded / offline on failure — the page never blanks out
// because a route is not live yet or the hub was slow.
function ep(kind){return '/review/'+kind+'/'+encodeURIComponent(D.id||'preview');}
const servedHost={mode:'served',
  load(){return fetch(ep('manifest'),{headers:{'accept':'application/json'}}).then(r=>r.ok?r.json():Promise.reject(r.status)).then(fresh=>{console.log('[preview] served: refreshed manifest');Object.assign(D,fresh);return D;}).catch(e=>{console.log('[preview] served load failed, using embedded:',e);return D;});},
  save(delta){console.log('[preview] served save: POST decisions',delta);return fetch(ep('decisions'),{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(delta)}).then(r=>r.json().then(res=>({ok:r.ok,res}))).then(o=>{const res=o.res||{};if(o.ok&&res.ok!==false){const bits=['added '+(res.added||0),'removed '+(res.removed||0)];if(res.alreadyPresent)bits.push(res.alreadyPresent+' already present');if(res.notFound)bits.push(res.notFound+' not found');banner('Saved: '+bits.join(', ')+'.',true);return this.load().then(refresh);}banner('Save rejected: '+esc(res.error||'hub returned an error')+' — delta downloaded as a fallback.',false);return offlineHost.save(delta);}).catch(e=>{console.log('[preview] POST failed, falling back to download:',e);return offlineHost.save(delta);});},
  rescan(){console.log('[preview] served: re-loading manifest');return this.load().then(refresh).then(()=>banner('Counts refreshed from the hub. A standalone re-preview trigger is planned; a Save already re-runs the preview.',true));}};
let host=OFFLINE?offlineHost:servedHost;

// ---- rendering -------------------------------------------------------------
let LONG=(D.limits&&D.limits.longTrackSecs)||1800;
function renderCounts(){const c=D.counts||{};document.getElementById('counts').innerHTML=num(c.libraryTotal)+' tracks · '+num(c.analyzed)+' analyzed · '+num(c.excluded)+' excluded · '+`<span class='badge'>${num(c.awaitingReview)} awaiting</span>`;document.getElementById('host').textContent='host: '+(OFFLINE?'offline':'served');}
function renderSummary(){const e=D.estimate||{},l=D.limits||{},sk=D.autoSkip||[];const skips=sk.length?sk.map(b=>num(b.count)+' '+esc(b.class)).join(' · '):'none';document.getElementById('summary').innerHTML=`<div><span class='k'>Estimate</span><b>${num(e.newTracks)}</b> new · <b>${bytes(e.newBytes)}</b> · ETA <b>${hms(e.etaSecs)}</b> <span class='ci'>(${esc(e.etaBasis||'unavailable')})</span></div>`+`<div><span class='k'>Limits</span>ceiling <b>${num(l.maxDurationSecs)}s</b> (${esc(l.maxDurationSource)}) · long ≥ <b>${Math.round((l.longTrackSecs||0)/60)} min</b> <span class='ci' title='${esc(l.extractorNote)}'>ⓘ</span></div>`+`<div><span class='k'>Auto-skipped</span><span class='ci' title='structural — a scan cannot analyze these; not reviewable'>can&#39;t analyze:</span> ${skips}</div>`;}
function renderBulk(){
  const genres=(D.genres||[]).filter(g=>g.name&&g.name!=='(none)');
  const chips=genres.length?genres.map(g=>{const on=desiredHas({kind:'genre',action:'exclude',value:g.name});return `<button class='chip${on?' excl':''}' data-genre='${esc(g.name)}'>${esc(g.name)} <span class='n'>×${num(g.tracks)}</span></button>`;}).join(''):'';
  const rules=(D.rules||[]).map(r=>{const ro=parseRuleStr(r.rule,r.action);const rm=!desired.has(idOf(ro));const stale=r.matchCount===0?` <span class='stale'>(0 — stale?)</span>`:` <span class='n'>(${num(r.matchCount)})</span>`;return `<div class='rule${rm?' pending-rm':''}'><span class='rn'>${esc(r.rule)}</span> <span class='n'>${esc(r.action)}</span>${stale}<button class='sec' data-rmrule='${esc(r.rule)}' data-rmact='${esc(r.action)}'>${rm?'keep':'remove'}</button></div>`;}).join('');
  document.getElementById('bulk').innerHTML=(chips?`<div class='sec-h'>Genres — click to exclude</div><div class='chips'>${chips}</div>`:'')+(rules?`<div class='sec-h'>Existing rules</div><div class='rules'>${rules}</div>`:'')+`<div class='sec-h'>Long-track filter (view only)</div><div class='slider'><input type='range' id='longr' min='0' max='7200' step='300' value='${LONG}'> ≥ <b id='longv'>${Math.round(LONG/60)}</b> min</div>`;
}
const RSN={'long':'long','over-limit':'over','speech-likely':'speech','excluded':'excl','podcast-labelled':'mark'};
function rsnClass(r){if(r.indexOf('marker:')===0)return 'mark';return RSN[r]||'';}
function decState(c){const x=desiredHas({kind:'file',action:'exclude',path:c.path});const i=desiredHas({kind:'file',action:'include',path:c.path});if(x)return 'x';if(i)return 'i';return c.currentDecision==='excluded'?'x0':c.currentDecision==='included'?'i0':'';}
function renderTable(){
  const body=document.getElementById('body');
  if(!(D.review||[]).length){body.innerHTML=`<div class='empty'>Nothing awaiting review.</div>`;return;}
  const rows=(D.review||[]).filter(c=>c.durationSecs>=LONG||c.overLimit||(c.reasons||[]).some(r=>r!=='long'&&r!=='over-limit'));
  const trunc=D.reviewTruncated?`<div class='trunc'>showing ${num(D.review.length)} of ${num(D.reviewTotal)} — narrow with the chips or the slider</div>`:'';
  if(!rows.length){body.innerHTML=trunc+`<div class='empty'>No candidates at the current long-track filter.</div>`;return;}
  const tr=rows.map(c=>{
    const st=decState(c);const cls=st==='x'||st==='x0'?'excl':st==='i'||st==='i0'?'incl':'';
    const badges=(c.reasons||[]).map(r=>`<span class='rsn ${rsnClass(r)}'>${esc(r)}</span>`).join('');
    const anc=ancestors(c.path);const opts=anc.map(a=>`<option value='${esc(a)}'>${esc(a)}</option>`).join('');
    return `<tr class='${cls}' data-p='${esc(c.path)}'><td class='dec'><button class='sec dx ${st==='x'?'on-x':''}'>excl</button><button class='sec di ${st==='i'?'on-i':''}'>incl</button>${anc.length?`<div class='fexcl'>folder: <select class='fsel'><option value=''>—</option>${opts}</select></div>`:''}</td><td class='path'><a class='flink' href='${esc(folderUrl(folderOf(c.path)))}' title='open folder'>${esc(c.path)}</a></td><td class='num'>${hms(c.durationSecs)}</td><td>${esc(c.genre)}</td><td>${esc(c.codec)}</td><td>${esc(c.state)}</td><td>${badges}</td></tr>`;
  }).join('');
  body.innerHTML=trunc+`<table><thead><tr><th>decision</th><th>path</th><th class='num'>len</th><th>genre</th><th>codec</th><th>state</th><th>reasons</th></tr></thead><tbody>${tr}</tbody></table>`;
}
function updateSave(){document.getElementById('save').disabled=!dirty();}
function refresh(){renderCounts();renderSummary();renderBulk();renderTable();updateSave();}

// ---- events ----------------------------------------------------------------
document.addEventListener('click',ev=>{
  const g=ev.target.closest('[data-genre]');if(g){toggleRule({kind:'genre',action:'exclude',value:g.getAttribute('data-genre')});return;}
  const rr=ev.target.closest('[data-rmrule]');if(rr){toggleRule(parseRuleStr(rr.getAttribute('data-rmrule'),rr.getAttribute('data-rmact')));return;}
  const row=ev.target.closest('tr[data-p]');if(row){const p=row.getAttribute('data-p');if(ev.target.classList.contains('dx')){setExclusive({kind:'file',action:'exclude',path:p});return;}if(ev.target.classList.contains('di')){setExclusive({kind:'file',action:'include',path:p});return;}}
});
document.addEventListener('input',ev=>{if(ev.target.id==='longr'){LONG=+ev.target.value;const v=document.getElementById('longv');if(v)v.textContent=Math.round(LONG/60);renderTable();}});
document.addEventListener('change',ev=>{if(ev.target.classList.contains('fsel')&&ev.target.value){toggleRule({kind:'folder',action:'exclude',pattern:ev.target.value});ev.target.value='';}});
document.getElementById('save').addEventListener('click',()=>{if(dirty())host.save(buildDelta());});
document.getElementById('rescan').addEventListener('click',()=>host.rescan());
document.getElementById('reset').addEventListener('click',()=>{desired=new Set(orig.keys());persist();document.getElementById('banner').style.display='none';refresh();});
refresh();
if(!OFFLINE)host.load().then(refresh);
</script>
</body>
</html>";
    }
}
