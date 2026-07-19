namespace NuGetAudit.Workbench;

internal static class GraphDocument
{
    internal static string Html => """
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8" />
  <style>
    body { font-family: Segoe UI, sans-serif; margin: 0; background: #fbfbfc; color: #1f2937; }
    #header { padding: 8px 12px; border-bottom: 1px solid #d4d4d8; background: linear-gradient(90deg, #f8fafc, #eef2ff); }
    #meta { font-size: 12px; color: #475569; }
    #canvas { width: 100%; height: calc(100vh - 40px); overflow: auto; }
    svg { width: 100%; height: 100%; min-height: 360px; }
    .edge { stroke: #94a3b8; stroke-width: 1.2; fill: none; opacity: 0.9; }
    .edge-label { font-size: 10px; fill: #64748b; }
    .node rect { rx: 10; ry: 10; stroke-width: 1.5; }
    .node.band-critical rect { fill: #fee2e2 !important; stroke: #7f1d1d !important; }
    .node.band-high rect { fill: #fef2f2 !important; stroke: #991b1b !important; }
    .node.band-attention rect { fill: #fff7ed !important; stroke: #92400e !important; }
    .node.band-ok rect { fill: #f0fdf4 !important; stroke: #166534 !important; }
    .node.band-none rect { fill: #ffffff !important; stroke: #64748b !important; }
    .node.graph-selected rect { stroke: #0f172a !important; stroke-width: 2.7 !important; }
    .node text { font-size: 11px; pointer-events: none; }
    #empty { padding: 16px; color: #64748b; }
  </style>
</head>
<body>
  <div id="header">
    <div id="title">Dependency graph</div>
    <div id="meta">Run an audit and select a project or package instance to focus the graph.</div>
  </div>
  <div id="canvas"><div id="empty">No dependency graph is available yet.</div></div>
  <script>
    function esc(v) { return String(v ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
    function graphNodeClasses(node) {
      const kind = String(node.referenceKind || '').toLowerCase();
      const bandRaw = String(node.rowVisualBand || 'None').toLowerCase();
      const band = ['critical','high','attention','ok','none'].includes(bandRaw) ? bandRaw : 'none';
      const parts = ['node', kind, 'band-' + band];
      if (node.selected) parts.push('graph-selected');
      return parts.filter(Boolean).join(' ').trim();
    }
    function layout(nodes, edges) {
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
      const positions = new Map(), spacingX = 250, spacingY = 108;
      for (const [d, level] of [...levels.entries()].sort((a,b)=>a[0]-b[0])) {
        level.sort((a,b)=>String(a.label).localeCompare(String(b.label)));
        level.forEach((node, index) => positions.set(node.id, { x: 40 + d * spacingX, y: 40 + index * spacingY }));
      }
      const width = Math.max(900, 120 + levels.size * spacingX);
      const maxCount = Math.max(1, ...[...levels.values()].map(level => level.length));
      const height = Math.max(360, 120 + maxCount * spacingY);
      return { positions, width, height };
    }
    window.renderGraph = function(payloadText) {
      const payload = JSON.parse(payloadText);
      document.getElementById('title').textContent = payload.projectName ? `${payload.projectName} dependency graph` : 'Dependency graph';
      document.getElementById('meta').textContent = payload.projectPath || 'Run an audit and select a project or package instance to focus the graph.';
      const canvas = document.getElementById('canvas');
      if (!payload.nodes || payload.nodes.length === 0) { canvas.innerHTML = '<div id="empty">No dependency graph is available for the current selection.</div>'; return; }
      const layoutResult = layout(payload.nodes, payload.edges || []);
      const parts = [`<svg viewBox="0 0 ${layoutResult.width} ${layoutResult.height}" xmlns="http://www.w3.org/2000/svg">`];
      for (const edge of payload.edges || []) {
        const from = layoutResult.positions.get(edge.from), to = layoutResult.positions.get(edge.to);
        if (!from || !to) continue;
        const x1 = from.x + 170, y1 = from.y + 24, x2 = to.x, y2 = to.y + 24, midX = (x1 + x2) / 2;
        parts.push(`<path class="edge" d="M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}" />`);
        parts.push(`<text class="edge-label" x="${midX}" y="${(y1 + y2) / 2 - 4}">${esc(edge.label)}</text>`);
      }
      for (const node of payload.nodes) {
        const pos = layoutResult.positions.get(node.id);
        if (!pos) continue;
        const css = graphNodeClasses(node);
        const lines = String(node.label || '').split('\n');
        parts.push(`<g class="${css}"><title>${esc(node.tooltip)}</title><rect x="${pos.x}" y="${pos.y}" width="170" height="52"></rect><text x="${pos.x + 10}" y="${pos.y + 20}">${esc(lines[0] || '')}</text><text x="${pos.x + 10}" y="${pos.y + 38}" fill="#64748b">${esc(lines[1] || node.tfm || '')}</text></g>`);
      }
      parts.push('</svg>');
      canvas.innerHTML = parts.join('');
    };
  </script>
</body>
</html>
""";
}
