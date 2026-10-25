using System.Globalization;
using System.Text;

namespace LanFileTransfer
{
    /// <summary>ブラウザ共有で表示する HTML ページ。</summary>
    public sealed partial class BrowserShareServer
    {
        private const string IndexPageStyle = """
body{margin:0;padding:24px;background:#eef1fa;font-family:'Segoe UI','Yu Gothic UI',sans-serif;color:#1e293b}
.card{max-width:640px;margin:0 auto;background:#fff;border:1px solid #e9edf7;border-radius:16px;padding:24px;box-shadow:0 10px 30px rgba(124,141,181,.18)}
h1{font-size:20px;margin:0 0 6px}
.note{color:#64748b;font-size:13px;line-height:1.7;margin:0 0 18px}
.toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin:0 0 14px}
.count{margin-left:auto;color:#64748b;font-size:12px;white-space:nowrap}
button{font:inherit;font-size:13px;font-weight:600;border-radius:10px;border:1px solid #c7d2fe;background:#eef2ff;color:#4338ca;padding:8px 14px;cursor:pointer}
button:hover:not(:disabled){background:#e0e7ff}
button:disabled{opacity:.45;cursor:not-allowed}
ul{list-style:none;margin:0;padding:0}
li{display:flex;align-items:center;gap:12px;padding:14px 16px;border:1px solid #e9edf7;border-radius:12px;margin-bottom:10px}
li.sel{border-color:#a5b4fc;background:#f5f7ff}
input[type=checkbox]{flex:0 0 auto;width:18px;height:18px;margin:0;accent-color:#4f46e5;cursor:pointer}
a{flex:1 1 auto;color:#4f46e5;font-weight:600;text-decoration:none;word-break:break-all}
a:hover{text-decoration:underline}
.size{flex:0 0 auto;color:#64748b;font-size:12px;white-space:nowrap}
.actions{display:flex;flex-wrap:wrap;gap:8px;margin:16px 0 0}
.actions button{flex:1 1 220px;padding:11px 14px}
.primary{background:#4f46e5;border-color:#4f46e5;color:#fff}
.primary:hover:not(:disabled){background:#4338ca}
.warn{margin:16px 0 0;color:#b45309;font-size:13px;min-height:1.2em}
.closed h1{color:#b91c1c}
""";

        private const string MessagePageStyle = """
body{margin:0;padding:24px;background:#eef1fa;font-family:'Segoe UI','Yu Gothic UI',sans-serif;color:#1e293b}
.card{max-width:640px;margin:0 auto;background:#fff;border:1px solid #e9edf7;border-radius:16px;padding:24px;box-shadow:0 10px 30px rgba(124,141,181,.18)}
h1{font-size:20px;margin:0 0 8px}
p{color:#64748b;font-size:14px;margin:0}
""";

        private const string IndexPageScript = """
(function(){var base='__BASE__',end=Date.now()+__REMAINING__;
  var boxes=Array.prototype.slice.call(document.querySelectorAll('.pick'));
  var selectAll=document.getElementById('selectAll');
  var clearAll=document.getElementById('clearAll');
  var downloadSelected=document.getElementById('downloadSelected');
  var downloadZip=document.getElementById('downloadZip');
  var count=document.getElementById('count');
  var state=document.getElementById('state');
  var left=document.getElementById('left');
  var stopped=false;

  function selected(){return boxes.filter(function(b){return b.checked;});}

  function refresh(){
    var n=selected().length;
    count.textContent='選択中: '+n+' 件';
    var idle=stopped||n===0;
    clearAll.disabled=idle;
    downloadSelected.disabled=idle;
    downloadZip.disabled=idle;
    selectAll.disabled=stopped||n===boxes.length;
    boxes.forEach(function(b){b.parentNode.classList.toggle('sel',b.checked);});
  }

  function startDownload(href){
    // download 属性付きのリンクを click() すると、自己署名証明書(HTTPS)の環境で
    // ブラウザに「安全でないダウンロード」としてキャンセルされることがある。
    // 非表示の iframe で添付ファイルを開くと、通常のダウンロードとして扱われる。
    var frame=document.createElement('iframe');
    frame.style.display='none';
    frame.src=href;
    document.body.appendChild(frame);
    setTimeout(function(){frame.remove();},300000);
  }

  boxes.forEach(function(b){b.addEventListener('change',refresh);});

  selectAll.addEventListener('click',function(){
    boxes.forEach(function(b){b.checked=true;});
    refresh();
  });

  clearAll.addEventListener('click',function(){
    boxes.forEach(function(b){b.checked=false;});
    refresh();
  });

  downloadSelected.addEventListener('click',function(){
    var list=selected();
    if(!list.length){return;}

    // 1件だけなら、ファイル名リンクをクリックしたときと同じトップレベル遷移で取得する。
    // (スクリプトで作ったリンクの click() は「自動ダウンロード」と見なされ、
    //  ブラウザの設定によっては何も起きないことがあるため)
    if(list.length===1){
      window.location.href=list[0].getAttribute('data-href');
      return;
    }

    // 複数: 1件目はファイル名リンクと同じトップレベル遷移で開始する。
    window.location.href=list[0].getAttribute('data-href');

    downloadSelected.disabled=true;
    if(state){state.textContent='選択したファイルのダウンロードを開始しました。保存されない場合は、ブラウザの「複数のファイルのダウンロード」を許可するか、ZIPにまとめてダウンロードしてください。';}

    list.slice(1).forEach(function(b,i){
      setTimeout(function(){startDownload(b.getAttribute('data-href'));},(i+1)*700);
    });

    setTimeout(function(){refresh();},list.length*700);
  });

  downloadZip.addEventListener('click',function(){
    var query=selected().map(function(b){return b.getAttribute('data-index');}).join(',');
    if(!query){return;}
    window.location.href=base+'zip?f='+query;
  });

  function tick(){
    var n=Math.max(0,Math.ceil((end-Date.now())/1000));
    if(left){left.textContent=String(n);}
    if(n<=0&&!stopped){
      stopped=true;
      document.body.classList.add('closed');
      if(state){state.textContent='共有は終了しました。ダウンロード中のファイルは完了まで続きます。';}
      refresh();
    }
  }

  tick();
  setInterval(tick,500);
  refresh();
})();
""";

        private string BuildIndexPage()
        {
            long remainingMs = (long)Math.Max(0, Remaining.TotalMilliseconds);
            string basePrefix = _useToken ? $"/s/{_token}/" : "/";

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>LAN File Transfer</title><style>").Append(IndexPageStyle).Append("</style>");
            sb.Append("</head><body><main class=\"card\">");
            sb.Append("<h1>ファイルを受け取る</h1>");
            sb.Append("<p class=\"note\">受け取りたいファイルにチェックを入れてください。<br>");
            sb.Append("ファイル名をクリックすると、そのファイルだけをすぐにダウンロードします。<br>");
            sb.Append("この共有は <strong id=\"left\">")
              .Append((long)Math.Ceiling(remainingMs / 1000.0))
              .Append("</strong> 秒後に終了します。</p>");
            sb.Append("<div class=\"toolbar\">");
            sb.Append("<button type=\"button\" id=\"selectAll\">全て選択</button>");
            sb.Append("<button type=\"button\" id=\"clearAll\">全て解除</button>");
            sb.Append("<span class=\"count\" id=\"count\">選択中: 0 件</span>");
            sb.Append("</div><ul>");

            AppendFileRows(sb, basePrefix);

            sb.Append("</ul><div class=\"actions\">");
            sb.Append("<button type=\"button\" class=\"primary\" id=\"downloadSelected\" disabled>選択したファイルをダウンロード</button>");
            sb.Append("<button type=\"button\" class=\"primary\" id=\"downloadZip\" disabled>ZIPにまとめてダウンロード</button>");
            sb.Append("</div><p class=\"warn\" id=\"state\"></p>");
            sb.Append("</main><script>")
              .Append(BuildIndexScript(basePrefix, remainingMs))
              .Append("</script></body></html>");
            return sb.ToString();
        }

        private void AppendFileRows(StringBuilder sb, string basePrefix)
        {
            for (int i = 0; i < _files.Count; i++)
            {
                ShareEntry entry = _files[i];
                string href = $"{basePrefix}f/{i}";
                string fileName = EscapeHtml(entry.FileName);

                sb.Append("<li><input type=\"checkbox\" class=\"pick\" data-index=\"").Append(i)
                  .Append("\" data-href=\"").Append(href).Append("\" aria-label=\"")
                  .Append(fileName).Append(" を選択\">");
                sb.Append("<a href=\"").Append(href).Append("\">")
                  .Append(fileName).Append("</a><span class=\"size\">")
                  .Append(ByteSize.Format(entry.Length)).Append("</span></li>");
            }
        }

        private static string BuildIndexScript(string basePrefix, long remainingMs)
        {
            return IndexPageScript
                .Replace("__BASE__", basePrefix)
                .Replace("__REMAINING__", remainingMs.ToString(CultureInfo.InvariantCulture));
        }

        private static string BuildMessagePage(string title, string message)
        {
            return "<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\">" +
                   "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                   "<title>LAN File Transfer</title><style>" + MessagePageStyle +
                   "</style></head><body><main class=\"card\"><h1>" + EscapeHtml(title) +
                   "</h1><p>" + EscapeHtml(message) + "</p></main></body></html>";
        }
    }
}
