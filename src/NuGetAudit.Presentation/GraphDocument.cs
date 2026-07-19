namespace NuGetAudit.Presentation;

public static class GraphDocument
{
    public static string Html => """
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8" />
  <style>
    html, body { height: 100%; margin: 0; }
    body { font-family: Segoe UI, sans-serif; display: flex; flex-direction: column; background: #fbfbfc; color: #1f2937; }
    #header { padding: 8px 12px; border-bottom: 1px solid #d4d4d8; background: linear-gradient(90deg, #f8fafc, #eef2ff); flex-shrink: 0; display: flex; flex-direction: column; gap: 8px; align-items: stretch; min-width: 0; }
    .header-text-row { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    #title { font-weight: 600; }
    #meta { font-size: 12px; color: #475569; word-break: break-word; }
    .header-toolbar-row { display: flex; flex-direction: row; align-items: center; min-width: 0; }
    .layout-toolbar { display: flex; align-items: center; gap: 8px; row-gap: 8px; flex-wrap: wrap; min-width: 0; }
    .layout-label { font-size: 12px; color: #64748b; font-weight: 600; }
    .layout-btn { padding: 4px 12px; font-size: 12px; border: 1px solid #cbd5e1; border-radius: 6px; background: #fff; cursor: pointer; color: #334155; }
    .layout-btn:hover:not(:disabled) { background: #f1f5f9; }
    .layout-btn.layout-btn-active { background: #e0e7ff; border-color: #6366f1; color: #312e81; font-weight: 600; }
    .layout-btn:disabled { opacity: 0.45; cursor: default; }
    .transient-toggle { display: flex; align-items: center; gap: 6px; margin-left: 12px; padding-left: 12px; border-left: 1px solid #cbd5e1; font-size: 12px; color: #334155; user-select: none; cursor: pointer; white-space: nowrap; }
    .transient-toggle input { cursor: pointer; flex-shrink: 0; }
    .transient-toggle.transient-toggle-disabled { opacity: 0.45; cursor: default; }
    .merge-net-toggle { display: flex; align-items: center; gap: 6px; margin-left: 12px; padding-left: 12px; border-left: 1px solid #cbd5e1; font-size: 12px; color: #334155; user-select: none; cursor: pointer; flex-shrink: 0; white-space: nowrap; }
    .merge-net-toggle .merge-net-toggle-text { white-space: nowrap; }
    .merge-net-toggle input { cursor: pointer; flex-shrink: 0; }
    .merge-net-toggle.merge-net-toggle-disabled { opacity: 0.45; cursor: default; }
    .copy-graph-wrap { display: flex; align-items: center; margin-left: 12px; padding-left: 12px; border-left: 1px solid #cbd5e1; flex-shrink: 0; }
    #canvas { width: 100%; flex: 1; min-height: 0; overflow: auto; overscroll-behavior: contain; }
    svg { width: 100%; height: 100%; min-height: 540px; }
    .edge { stroke: #94a3b8; stroke-width: 1.2; fill: none; opacity: 0.9; }
    .edge.edge-transitive { stroke-dasharray: 7 5; }
    .edge-label { font-size: 10px; fill: #64748b; }
    .node rect { rx: 10; ry: 10; stroke-width: 1.5; }
    .node.band-critical rect { fill: #fee2e2 !important; stroke: #7f1d1d !important; }
    .node.band-high rect { fill: #fef2f2 !important; stroke: #991b1b !important; }
    .node.band-attention rect { fill: #fff7ed !important; stroke: #92400e !important; }
    .node.band-ok rect { fill: #f0fdf4 !important; stroke: #166534 !important; }
    .node.band-none rect { fill: #ffffff !important; stroke: #64748b !important; }
    .node.project rect { fill: #eef2ff !important; stroke: #4338ca !important; }
    .node.graph-selected rect { stroke: #0f172a !important; stroke-width: 2.7 !important; }
    .node text { font-size: 11px; pointer-events: none; }
    .node tspan.secondary { fill: #64748b; }
    #empty { padding: 16px; color: #64748b; }
  </style>
</head>
<body>
  <div id="header">
    <div class="header-text-row">
      <div id="title">Dependency graph</div>
      <div id="meta">Select a project or package instance to focus the graph.</div>
    </div>
    <div class="header-toolbar-row">
      <div class="layout-toolbar">
        <span class="layout-label">Layout</span>
        <button type="button" id="btnLayoutHorizontal" class="layout-btn layout-btn-active" disabled>Horizontal</button>
        <button type="button" id="btnLayoutVertical" class="layout-btn" disabled>Vertical</button>
        <label id="lblShowTransient" class="transient-toggle transient-toggle-disabled" title="When off, transitive package dependencies are hidden from the graph.">
          <input type="checkbox" id="chkShowTransient" disabled />
          <span>Show Transient</span>
        </label>
        <label id="lblMergeNetVersions" class="merge-net-toggle" title="When checked, package rows that differ only by target framework are merged in the package table and dependency graph. The dependency tree is unchanged.">
          <input type="checkbox" id="chkMergeNetVersions" checked />
          <span class="merge-net-toggle-text">Merge .NET Versions</span>
        </label>
        <div class="copy-graph-wrap">
          <button type="button" id="btnCopyGraph" class="layout-btn" disabled title="Copy the current graph as a PNG image to the clipboard.">Copy image</button>
        </div>
      </div>
    </div>
  </div>
  <div id="canvas"><div id="empty">No dependency graph is available yet.</div></div>
  <script>
    function esc(v) { return String(v ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
    const NODE_MIN_W = 170, NODE_MAX_W = 300, NODE_PAD_X = 10, NODE_PAD_Y = 8, NODE_LINE_H = 13;
    const NODE_MAX_PRIMARY_LINES = 6, NODE_MAX_SECONDARY_LINES = 3;
    const CHAR_PX_EST = 6.15;

    function middleEllipsis(s, maxLen) {
      s = String(s);
      if (s.length <= maxLen) return s;
      if (maxLen <= 5) return s.slice(0, Math.max(1, maxLen - 1)) + '\u2026';
      const inner = maxLen - 3;
      const left = Math.max(1, Math.ceil(inner / 2));
      const right = Math.max(1, Math.floor(inner / 2));
      return s.slice(0, left) + '...' + s.slice(s.length - right);
    }

    function wrapTextToLines(str, maxChars) {
      str = String(str || '').trim();
      if (!str) return [];
      const words = str.split(/\s+/).filter(Boolean);
      const lines = [];
      let cur = '';
      for (let word of words) {
        if (word.length > maxChars) word = middleEllipsis(word, maxChars);
        if (!cur) { cur = word; continue; }
        if (cur.length + 1 + word.length <= maxChars) cur += ' ' + word;
        else { lines.push(cur); cur = word; }
      }
      if (cur) lines.push(cur);
      return lines;
    }

    function capLines(lines, maxLines, maxChars) {
      if (lines.length <= maxLines) return lines;
      const head = lines.slice(0, maxLines - 1);
      const tail = lines.slice(maxLines - 1).join(' ');
      head.push(middleEllipsis(tail, maxChars));
      return head;
    }

    function splitNodeLabelParts(node) {
      const parts = String(node.label || '').split('\n');
      return { primary: (parts[0] || '').trim(), secondary: (parts[1] || node.tfm || '').trim() };
    }

    function buildNodeDisplay(node) {
      const { primary, secondary } = splitNodeLabelParts(node);
      function attempt(widthPx) {
        const maxChars = Math.max(8, Math.floor((widthPx - 2 * NODE_PAD_X) / CHAR_PX_EST));
        let pLines = wrapTextToLines(primary, maxChars);
        let sLines = secondary ? wrapTextToLines(secondary, maxChars) : [];
        pLines = capLines(pLines, NODE_MAX_PRIMARY_LINES, maxChars);
        sLines = capLines(sLines, NODE_MAX_SECONDARY_LINES, maxChars);
        let h = NODE_PAD_Y * 2 + pLines.length * NODE_LINE_H;
        if (sLines.length) h += 2 + sLines.length * NODE_LINE_H;
        h = Math.max(40, h + 4);
        return { w: widthPx, h, primaryLines: pLines, secondaryLines: sLines };
      }
      let meta = attempt(NODE_MIN_W);
      const wantsWider = meta.primaryLines.length >= 4 || meta.secondaryLines.length >= 2;
      if (wantsWider) {
        for (let w = NODE_MIN_W + 20; w <= NODE_MAX_W; w += 20) {
          const next = attempt(w);
          meta = next;
          if (meta.primaryLines.length <= 3 && meta.secondaryLines.length <= 2) break;
        }
      }
      return meta;
    }

    function buildNodeTextSvg(pos, meta) {
      const x = pos.x + NODE_PAD_X;
      let y = pos.y + NODE_PAD_Y + 11;
      const tspans = [];
      for (const line of meta.primaryLines) {
        tspans.push(`<tspan x="${x}" y="${y}">${esc(line)}</tspan>`);
        y += NODE_LINE_H;
      }
      if (meta.secondaryLines.length) {
        y += 2;
        for (const line of meta.secondaryLines) {
          tspans.push(`<tspan class="secondary" x="${x}" y="${y}">${esc(line)}</tspan>`);
          y += NODE_LINE_H;
        }
      }
      return `<text>${tspans.join('')}</text>`;
    }

    function graphNodeClasses(node) {
      const kind = String(node.referenceKind || '').toLowerCase();
      const bandRaw = String(node.rowVisualBand || 'None').toLowerCase();
      const band = ['critical','high','attention','ok','none'].includes(bandRaw) ? bandRaw : 'none';
      const parts = ['node', kind, 'band-' + band];
      if (node.selected) parts.push('graph-selected');
      return parts.filter(Boolean).join(' ').trim();
    }

    let graphLayoutTheme = 'horizontal';
    let lastGraphPayloadText = null;
    let graphZoom = 1;
    const GRAPH_ZOOM_MIN = 0.2;
    const GRAPH_ZOOM_MAX = 4;

    function updateLayoutToolbar() {
      const h = document.getElementById('btnLayoutHorizontal');
      const v = document.getElementById('btnLayoutVertical');
      if (!h || !v) return;
      h.classList.toggle('layout-btn-active', graphLayoutTheme === 'horizontal');
      v.classList.toggle('layout-btn-active', graphLayoutTheme === 'vertical');
    }

    let mergeNetVersionsSyncFromHost = false;

    function postMergeNetVersionsToHost() {
      if (!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage)) return;
      const el = document.getElementById('chkMergeNetVersions');
      const value = el && el.checked === true;
      window.chrome.webview.postMessage(JSON.stringify({ type: 'mergeNetVersions', value }));
    }

    window.applyMergeNetVersionsFromHost = function(checked) {
      const el = document.getElementById('chkMergeNetVersions');
      if (!el) return;
      mergeNetVersionsSyncFromHost = true;
      el.checked = !!checked;
      mergeNetVersionsSyncFromHost = false;
    };

    function setLayoutButtonsEnabled(on) {
      const h = document.getElementById('btnLayoutHorizontal');
      const v = document.getElementById('btnLayoutVertical');
      const chk = document.getElementById('chkShowTransient');
      const lbl = document.getElementById('lblShowTransient');
      if (h) h.disabled = !on;
      if (v) v.disabled = !on;
      if (chk) chk.disabled = !on;
      if (lbl) lbl.classList.toggle('transient-toggle-disabled', !on);
    }

    function setCopyGraphButtonEnabled(on) {
      const b = document.getElementById('btnCopyGraph');
      if (b) b.disabled = !on;
    }

    const GRAPH_EXPORT_SCALE = 2;
    const GRAPH_EXPORT_STYLES = `
svg { font-family: Segoe UI, sans-serif; }
.edge { stroke: #94a3b8; stroke-width: 1.2; fill: none; opacity: 0.9; }
.edge.edge-transitive { stroke-dasharray: 7 5; }
.edge-label { font-size: 10px; fill: #64748b; }
.node rect { rx: 10; ry: 10; stroke-width: 1.5; }
.node.band-critical rect { fill: #fee2e2 !important; stroke: #7f1d1d !important; }
.node.band-high rect { fill: #fef2f2 !important; stroke: #991b1b !important; }
.node.band-attention rect { fill: #fff7ed !important; stroke: #92400e !important; }
.node.band-ok rect { fill: #f0fdf4 !important; stroke: #166534 !important; }
.node.band-none rect { fill: #ffffff !important; stroke: #64748b !important; }
.node.project rect { fill: #eef2ff !important; stroke: #4338ca !important; }
.node.graph-selected rect { stroke: #0f172a !important; stroke-width: 2.7 !important; }
.node text { font-size: 11px; pointer-events: none; }
.node tspan.secondary { fill: #64748b; }
`;

    function postGraphCopyResult(ok, message) {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'graphCopyResult', ok: !!ok, message: message || '' }));
      }
    }

    window.__notifyGraphCopied = function() {
      const btn = document.getElementById('btnCopyGraph');
      if (!btn) return;
      const prev = btn.dataset.copyLabelDefault || btn.textContent;
      btn.dataset.copyLabelDefault = prev;
      btn.textContent = 'Copied!';
      setTimeout(function() { btn.textContent = prev; }, 2000);
    };

    function copyGraphSvgAsPng() {
      return new Promise((resolve, reject) => {
        if (!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage)) {
          reject(new Error('Graph copy requires the embedded browser host.'));
          return;
        }
        const canvasEl = document.getElementById('canvas');
        const svg = canvasEl && canvasEl.querySelector('svg');
        if (!svg || !svg.viewBox || !svg.viewBox.baseVal) {
          reject(new Error('No graph to copy.'));
          return;
        }
        const vb = svg.viewBox.baseVal;
        const w = vb.width;
        const h = vb.height;
        if (w <= 0 || h <= 0) {
          reject(new Error('No graph to copy.'));
          return;
        }
        const inner = svg.innerHTML;
        const exportSvg = '<?xml version="1.0" encoding="UTF-8"?><svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '" viewBox="0 0 ' + w + ' ' + h + '"><defs><style type="text/css"><![CDATA[' + GRAPH_EXPORT_STYLES + ']]></style></defs>' + inner + '</svg>';
        const img = new Image();
        const dataUrl = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(exportSvg);
        img.onload = function() {
          try {
            const scale = GRAPH_EXPORT_SCALE;
            const out = document.createElement('canvas');
            out.width = Math.ceil(w * scale);
            out.height = Math.ceil(h * scale);
            const ctx = out.getContext('2d');
            if (!ctx) {
              reject(new Error('Canvas is not available.'));
              return;
            }
            ctx.fillStyle = '#ffffff';
            ctx.fillRect(0, 0, out.width, out.height);
            ctx.drawImage(img, 0, 0, out.width, out.height);
            out.toBlob(function(pngBlob) {
              if (!pngBlob) {
                reject(new Error('PNG conversion failed.'));
                return;
              }
              const reader = new FileReader();
              reader.onloadend = function() {
                const result = reader.result;
                if (typeof result !== 'string') {
                  reject(new Error('Could not read PNG data.'));
                  return;
                }
                window.chrome.webview.postMessage(JSON.stringify({ type: 'graphCopyPngDataUrl', dataUrl: result }));
                resolve();
              };
              reader.onerror = function() {
                reject(new Error('Could not read PNG data.'));
              };
              reader.readAsDataURL(pngBlob);
            }, 'image/png');
          } catch (err) {
            reject(err);
          }
        };
        img.onerror = function() {
          reject(new Error('Could not rasterize the graph (SVG).'));
        };
        img.src = dataUrl;
      });
    }

    function applyTransientVisibilityFilter(payload) {
      const chk = document.getElementById('chkShowTransient');
      const show = chk && chk.checked === true;
      const base = {
        projectName: payload.projectName,
        projectPath: payload.projectPath,
        nodes: payload.nodes || [],
        edges: payload.edges || []
      };
      if (show) return base;
      const nodes = base.nodes.filter(n => !n.transitive);
      const ids = new Set(nodes.map(n => n.id));
      const edges = base.edges.filter(e => ids.has(e.from) && ids.has(e.to));
      return { projectName: base.projectName, projectPath: base.projectPath, nodes, edges };
    }

    function layout(nodes, edges, maxNodeW, maxNodeH, theme) {
      const children = new Map(), indegree = new Map(), depth = new Map();
      for (const n of nodes) { children.set(n.id, []); indegree.set(n.id, 0); }
      for (const e of edges) {
        if (!children.has(e.from) || !children.has(e.to)) continue;
        children.get(e.from).push(e.to);
        indegree.set(e.to, (indegree.get(e.to) || 0) + 1);
      }
      const q = [];
      for (const n of nodes) if ((indegree.get(n.id) || 0) === 0) { q.push(n.id); depth.set(n.id, 0); }
      while (q.length) {
        const current = q.shift(), d = depth.get(current) || 0;
        for (const child of children.get(current) || []) {
          depth.set(child, Math.max(depth.get(child) || 0, d + 1));
          indegree.set(child, (indegree.get(child) || 0) - 1);
          if ((indegree.get(child) || 0) <= 0) q.push(child);
        }
      }
      const levels = new Map();
      for (const n of nodes) {
        const d = depth.get(n.id) || 0;
        if (!levels.has(d)) levels.set(d, []);
        levels.get(d).push(n);
      }
      const layerGapHorizontal = Math.max(260, maxNodeW + 55);
      const peerGapHorizontal = Math.max(100, maxNodeH + 28);
      const layerGapVertical = Math.max(220, maxNodeH + 50);
      const peerGapVertical = Math.max(120, maxNodeW + 35);
      const positions = new Map();
      for (const [d, level] of [...levels.entries()].sort((a,b)=>a[0]-b[0])) {
        level.sort((a,b)=>String(a.label).localeCompare(String(b.label)));
        level.forEach((node, index) => {
          if (theme === 'horizontal') {
            positions.set(node.id, { x: 40 + index * peerGapVertical, y: 40 + d * layerGapVertical });
          } else {
            positions.set(node.id, { x: 40 + d * layerGapHorizontal, y: 40 + index * peerGapHorizontal });
          }
        });
      }
      const maxCount = Math.max(1, ...[...levels.values()].map(level => level.length));
      let width, height;
      if (theme === 'horizontal') {
        width = Math.max(900, 120 + maxCount * peerGapVertical);
        height = Math.max(540, 120 + levels.size * layerGapVertical);
      } else {
        width = Math.max(900, 120 + levels.size * layerGapHorizontal);
        height = Math.max(540, 120 + maxCount * peerGapHorizontal);
      }
      return { positions, width, height };
    }

    function edgePathAndLabel(from, to, fromM, toM, theme) {
      if (theme === 'horizontal') {
        const x1 = from.x + fromM.w / 2, y1 = from.y + fromM.h, x2 = to.x + toM.w / 2, y2 = to.y;
        const midY = (y1 + y2) / 2;
        return {
          d: `M ${x1} ${y1} C ${x1} ${midY}, ${x2} ${midY}, ${x2} ${y2}`,
          lx: (x1 + x2) / 2,
          ly: midY - 4
        };
      }
      const x1 = from.x + fromM.w, y1 = from.y + fromM.h / 2, x2 = to.x, y2 = to.y + toM.h / 2;
      const midX = (x1 + x2) / 2;
      return {
        d: `M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}`,
        lx: midX,
        ly: (y1 + y2) / 2 - 4
      };
    }

    function onGraphWheelCapture(e) {
      const canvas = document.getElementById('canvas');
      if (!canvas || !canvas.contains(e.target)) {
        return;
      }
      const svg = canvas.querySelector('svg');
      if (!svg || !svg.viewBox || !svg.viewBox.baseVal) {
        return;
      }
      e.preventDefault();
      e.stopPropagation();
      const vb = svg.viewBox.baseVal;
      const layoutW = vb.width;
      const layoutH = vb.height;
      if (layoutW <= 0 || layoutH <= 0) {
        return;
      }
      const oldZoom = graphZoom;
      const factor = Math.exp(-e.deltaY * 0.0015);
      let nextZoom = graphZoom * factor;
      if (nextZoom < GRAPH_ZOOM_MIN) nextZoom = GRAPH_ZOOM_MIN;
      if (nextZoom > GRAPH_ZOOM_MAX) nextZoom = GRAPH_ZOOM_MAX;
      if (Math.abs(nextZoom - oldZoom) < 1e-6) {
        return;
      }
      const scale = nextZoom / oldZoom;
      const rect = canvas.getBoundingClientRect();
      const viewX = e.clientX - rect.left + canvas.scrollLeft;
      const viewY = e.clientY - rect.top + canvas.scrollTop;
      graphZoom = nextZoom;
      svg.setAttribute('width', String(layoutW * graphZoom));
      svg.setAttribute('height', String(layoutH * graphZoom));
      canvas.scrollLeft = viewX * scale - (e.clientX - rect.left);
      canvas.scrollTop = viewY * scale - (e.clientY - rect.top);
    }

    window.addEventListener('wheel', onGraphWheelCapture, { passive: false, capture: true });

    window.renderGraph = function(payloadText) {
      const rawPayload = JSON.parse(payloadText);
      document.getElementById('title').textContent = rawPayload.projectName ? `${rawPayload.projectName} dependency graph` : 'Dependency graph';
      document.getElementById('meta').textContent = rawPayload.projectPath || 'Select a project or package instance to focus the graph.';
      const canvas = document.getElementById('canvas');
      if (!rawPayload.nodes || rawPayload.nodes.length === 0) {
        graphZoom = 1;
        lastGraphPayloadText = payloadText;
        canvas.innerHTML = '<div id="empty">No dependency graph is available for the current selection.</div>';
        setLayoutButtonsEnabled(false);
        setCopyGraphButtonEnabled(false);
        return;
      }
      if (lastGraphPayloadText !== payloadText) {
        graphZoom = 1;
      }
      lastGraphPayloadText = payloadText;
      const payload = applyTransientVisibilityFilter(rawPayload);
      if (!payload.nodes || payload.nodes.length === 0) {
        graphZoom = 1;
        canvas.innerHTML = '<div id="empty">Nothing to display with transitive dependencies hidden. Turn on <b>Show Transient</b> to include them.</div>';
        setLayoutButtonsEnabled(true);
        setCopyGraphButtonEnabled(false);
        updateLayoutToolbar();
        return;
      }
      const metaById = new Map();
      let maxNodeW = NODE_MIN_W, maxNodeH = 52;
      for (const node of payload.nodes) {
        const meta = buildNodeDisplay(node);
        metaById.set(node.id, meta);
        maxNodeW = Math.max(maxNodeW, meta.w);
        maxNodeH = Math.max(maxNodeH, meta.h);
      }
      const layoutResult = layout(payload.nodes, payload.edges || [], maxNodeW, maxNodeH, graphLayoutTheme);
      const zw = layoutResult.width * graphZoom;
      const zh = layoutResult.height * graphZoom;
      const parts = [`<svg width="${zw}" height="${zh}" viewBox="0 0 ${layoutResult.width} ${layoutResult.height}" xmlns="http://www.w3.org/2000/svg">`];
      for (const edge of payload.edges || []) {
        const from = layoutResult.positions.get(edge.from), to = layoutResult.positions.get(edge.to);
        const fromM = metaById.get(edge.from), toM = metaById.get(edge.to);
        if (!from || !to || !fromM || !toM) continue;
        const ep = edgePathAndLabel(from, to, fromM, toM, graphLayoutTheme);
        const edgeClass = edge.transitive ? 'edge edge-transitive' : 'edge';
        parts.push(`<path class="${edgeClass}" d="${ep.d}" />`);
        if (edge.label != null && String(edge.label).trim() !== '') {
          parts.push(`<text class="edge-label" x="${ep.lx}" y="${ep.ly}">${esc(edge.label)}</text>`);
        }
      }
      for (const node of payload.nodes) {
        const pos = layoutResult.positions.get(node.id);
        if (!pos) continue;
        const meta = metaById.get(node.id);
        if (!meta) continue;
        const css = graphNodeClasses(node);
        parts.push(`<g class="${css}"><title>${esc(node.tooltip)}</title><rect x="${pos.x}" y="${pos.y}" width="${meta.w}" height="${meta.h}"></rect>${buildNodeTextSvg(pos, meta)}</g>`);
      }
      parts.push('</svg>');
      canvas.innerHTML = parts.join('');
      setLayoutButtonsEnabled(true);
      setCopyGraphButtonEnabled(true);
      updateLayoutToolbar();
    };

    document.getElementById('btnLayoutHorizontal').addEventListener('click', () => {
      if (graphLayoutTheme === 'horizontal' || !lastGraphPayloadText) return;
      graphLayoutTheme = 'horizontal';
      updateLayoutToolbar();
      window.renderGraph(lastGraphPayloadText);
    });
    document.getElementById('btnLayoutVertical').addEventListener('click', () => {
      if (graphLayoutTheme === 'vertical' || !lastGraphPayloadText) return;
      graphLayoutTheme = 'vertical';
      updateLayoutToolbar();
      window.renderGraph(lastGraphPayloadText);
    });
    document.getElementById('chkShowTransient').addEventListener('change', () => {
      if (!lastGraphPayloadText) return;
      window.renderGraph(lastGraphPayloadText);
    });
    document.getElementById('chkMergeNetVersions').addEventListener('change', () => {
      if (mergeNetVersionsSyncFromHost) return;
      postMergeNetVersionsToHost();
    });
    document.getElementById('btnCopyGraph').addEventListener('click', () => {
      const btn = document.getElementById('btnCopyGraph');
      if (!btn || btn.disabled) return;
      copyGraphSvgAsPng().catch((e) => {
        const msg = (e && e.message) ? e.message : String(e);
        postGraphCopyResult(false, msg);
      });
    });
  </script>
</body>
</html>
""";
}
