using System;
using System.Collections.Generic;
using System.Text;

namespace MarkSmith.Core.Office
{
    /// <summary>
    /// Builds the self-contained preview page for "Word-exact" mode: the plugin's real Word
    /// render as a GRID of page-band PNG tiles (data URIs) stacked like the actual pages —
    /// each band refreshes in place, so an edit only swaps the tiles it touched. When Looking
    /// Glass (portal) mode is on, the page includes the same portal overlay + JS API
    /// (__portalSetBlur / __portalSetShape / __portalSetReveal) so the app's existing
    /// blur/unblur (Ctrl+Alt+X) and aperture controls keep working on the Word-accurate view.
    /// </summary>
    public static class WordFidelityPage
    {
        public static string Build(
            IReadOnlyList<byte[]>? pagePngs,
            bool lookingGlassMode,
            bool stale,
            IReadOnlySet<int>? refreshing = null)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><style>");
            sb.Append(@"
html,body{margin:0;height:100%;background:#18181c;overflow:hidden;font-family:system-ui;}
#canvas{position:absolute;inset:0;display:flex;align-items:flex-start;justify-content:center;overflow:auto;transition:filter .22s ease;padding:16px;box-sizing:border-box;}
.fidelity-tiles{display:flex;flex-direction:column;gap:10px;background:#18181c;}
.fidelity-tile{position:relative;background:#fff;box-shadow:0 4px 24px rgba(0,0,0,.45);border-radius:4px;overflow:hidden;}
.fidelity-tile img{display:block;width:100%;}
.tile-refreshing{position:absolute;inset:0;background:rgba(24,24,28,.55);display:flex;align-items:center;justify-content:center;color:#fff;font-size:13px;letter-spacing:.5px;}
.badge{position:fixed;top:10px;left:50%;transform:translateX(-50%);background:rgba(0,120,212,.92);color:#fff;padding:5px 14px;border-radius:999px;font-size:12px;z-index:50;box-shadow:0 2px 10px rgba(0,0,0,.4);}
.badge.stale{background:rgba(214,83,33,.95);}
.portal-aperture{position:fixed;inset:0;z-index:40;pointer-events:none;display:flex;align-items:center;justify-content:center;}
.portal-aperture .ring{position:absolute;border-radius:50%;box-shadow:0 0 0 2000px rgba(24,24,28,.92);backdrop-filter:blur(5px);transition:all .25s ease;}
.portal-aperture .ring.square{border-radius:24px;}
.portal-aperture .ring.focus1{border-radius:50%;box-shadow:0 0 0 2000px rgba(24,24,28,.92),0 0 0 6px rgba(0,120,212,.8);}
.portal-aperture .ring.logo{clip-path:polygon(50% 0,100% 50%,50% 100%,0 50%);border-radius:0;}
.aperture-hidden .portal-aperture{opacity:0;transition:opacity .25s ease;}
");
            sb.Append("</style></head><body>");

            if (lookingGlassMode)
            {
                sb.Append(@"<div class=""portal-aperture""><div class=""ring"" id=""apertureRing""></div></div>");
            }

            sb.Append("<div id=\"canvas\"><div class=\"fidelity-tiles\">");
            if (pagePngs != null)
            {
                for (int i = 0; i < pagePngs.Count; i++)
                {
                    string uri = "data:image/png;base64," + Convert.ToBase64String(pagePngs[i]);
                    sb.Append("<div class=\"fidelity-tile\" data-page=\"").Append(i + 1).Append("\">");
                    sb.Append("<img src=\"").Append(uri).Append("\" alt=\"Word render page ")
                      .Append(i + 1).Append("\" draggable=\"false\"/>");
                    if (refreshing != null && refreshing.Contains(i + 1))
                    {
                        sb.Append("<div class=\"tile-refreshing\" data-page=\"").Append(i + 1)
                          .Append("\">Refreshing…</div>");
                    }
                    sb.Append("</div>");
                }
            }
            sb.Append("</div></div>");

            if (stale)
            {
                sb.Append("<div class=\"badge stale\">Word render out of date — edit and toggle Word-exact off/on to refresh</div>");
            }
            else
            {
                sb.Append("<div class=\"badge\">Word-accurate render</div>");
            }

            sb.Append(@"<script>
let apertureSize = Math.min(window.innerWidth, window.innerHeight) * 0.46;
const ring = document.getElementById('apertureRing');
function applyAperture(){ if(!ring) return; ring.style.width = apertureSize+'px'; ring.style.height = apertureSize+'px'; }
applyAperture(); window.addEventListener('resize', applyAperture);
window.__portalSetBlur = function(on){
  const canvas = document.getElementById('canvas');
  if (canvas) canvas.style.filter = on ? 'blur(6px)' : '';
};
window.__portalSetShape = function(shape){
  if(!ring) return;
  ring.className = 'ring' + (shape === 'square' ? ' square' : shape === 'focus1' ? ' focus1' : shape === 'logo' ? ' logo' : '');
};
window.__portalSetReveal = function(reveal){
  document.body.classList.toggle('aperture-hidden', !!reveal);
};
window.__portalSetSize = function(scale){
  apertureSize = Math.max(60, Math.min(window.innerWidth, window.innerHeight) * scale);
  applyAperture();
};
window.__tileRefreshing = function(page, on){
  var sel = '.fidelity-tile[data-page=' + page + '] .tile-refreshing';
  document.querySelectorAll(sel).forEach(function(el){ el.style.display = on ? 'flex' : 'none'; });
};
</script></body></html>");
            return sb.ToString();
        }
    }
}
