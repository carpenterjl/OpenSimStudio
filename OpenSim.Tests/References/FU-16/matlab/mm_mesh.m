function G = mm_mesh(regions, domain, opts)
%MM_MESH femodel (magnetostatic) geometry + quadratic mesh of a rectangular
% domain with rectangular regions; faces/edges classified (see mm_classify).
% opts: axi, hmax, hregion, hvertex [x y h], hgrad. Returns G.model (femodel,
% meshed, no materials yet), G.msh, G.faceRegion, G.edgeSide, G.elemRegion.
if ~isfield(opts, 'hgrad'), opts.hgrad = 1.3; end
dl = mm_geom(regions, domain);
gm = fegeometry(dl);
m = femodel(AnalysisType="magnetostatic", Geometry=gm);
if isfield(opts, 'axi') && opts.axi, m.PlanarType = "axisymmetric"; end
m.VacuumPermeability = 4e-7*pi;
span = max(domain(2) - domain(1), domain(4) - domain(3));
mc = generateMesh(m, Hmax=span/8, GeometricOrder="linear");
[faceRegion, edgeSide] = mm_classify(mc.Geometry.Mesh, gm.NumFaces, gm.NumEdges, regions, domain);
hface = {};
for k = 1:numel(regions)
  if isfield(opts, 'hregion') && k <= numel(opts.hregion) && ~isnan(opts.hregion(k))
    fids = find(faceRegion == k);
    if ~isempty(fids), hface(end+1:end+2) = {fids, opts.hregion(k)}; end %#ok<AGROW>
  end
end
args = {'Hmax', opts.hmax, 'Hgrad', opts.hgrad};
if ~isempty(hface), args = [args, {'Hface', hface}]; end
if isfield(opts, 'hvertex') && ~isempty(opts.hvertex)
  V = gm.Vertices;
  hv = {};
  for i = 1:size(opts.hvertex, 1)
    d = hypot(V(:,1) - opts.hvertex(i,1), V(:,2) - opts.hvertex(i,2));
    [dmin, vid] = min(d);
    if dmin > 1e-9 * span, error('mm_mesh: no vertex at (%g, %g)', opts.hvertex(i,1), opts.hvertex(i,2)); end
    hv(end+1:end+2) = {vid, opts.hvertex(i,3)}; %#ok<AGROW>
  end
  args = [args, {'Hvertex', hv}];
end
m = generateMesh(m, args{:});
msh = m.Geometry.Mesh;
elemRegion = zeros(1, size(msh.Elements, 2));
for f = 1:gm.NumFaces
  elemRegion(findElements(msh, "region", Face=f)) = faceRegion(f);
end
G.model = m; G.msh = msh; G.faceRegion = faceRegion; G.edgeSide = edgeSide; G.elemRegion = elemRegion;
G.nodes = size(msh.Nodes, 2); G.elements = size(msh.Elements, 2);
end
