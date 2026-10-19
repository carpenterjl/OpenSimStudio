function S = mm_ms_solve(regions, domain, opts)
%MM_MS_SOLVE Magnetostatic solve with femodel (PDE Toolbox), planar or
% axisymmetric, on a rectangular domain with rectangular regions.
%  regions: struct array x0,x1,y0,y1, mur (number or @(loc,state) handle), J [A/m^2]
%  domain : [x0 x1 y0 y1]
%  opts.axi       : true for axisymmetric (r = x >= 0)
%  opts.dirichlet : sides with A = 0, e.g. 'TBR' (the axis is pinned by the toolbox)
%  opts.hmax      : global maximum element size
%  opts.hregion   : per-region target size (vector, NaN = none)
%  opts.hvertex   : [x y h] rows: refine at the geometry vertex nearest (x, y)
%  opts.hgrad     : mesh growth (default 1.3)
%  opts.solver    : optional struct of SolverOptions fields (nonlinear)
% Returns S.msh, S.faceRegion, S.u (nodal psi = r*A_phi, or A_z planar),
% S.elemRegion (region index per element, 0 = background), S.nodes, S.time.
t0 = tic;
if ~isfield(opts, 'hgrad'), opts.hgrad = 1.3; end
dl = mm_geom(regions, domain);
gm = fegeometry(dl);
m = femodel(AnalysisType="magnetostatic", Geometry=gm);
if opts.axi, m.PlanarType = "axisymmetric"; end
m.VacuumPermeability = 4e-7*pi;
% classify faces and edges on a coarse mesh (face/edge IDs are geometric)
span = max(domain(2) - domain(1), domain(4) - domain(3));
mc = generateMesh(m, Hmax=span/8, GeometricOrder="linear");
[faceRegion, edgeSide] = mm_classify(mc.Geometry.Mesh, gm.NumFaces, gm.NumEdges, regions, domain);
% materials and sources per face
for f = 1:gm.NumFaces
  k = faceRegion(f);
  if k == 0
    m.MaterialProperties(f) = materialProperties(RelativePermeability=1);
  else
    m.MaterialProperties(f) = materialProperties(RelativePermeability=regions(k).mur);
    if isfield(regions, 'J') && ~isempty(regions(k).J) && regions(k).J ~= 0
      m.FaceLoad(f) = faceLoad(CurrentDensity=regions(k).J);
    end
  end
end
for k = 1:gm.NumEdges
  if any(edgeSide(k) == opts.dirichlet)
    m.EdgeBC(k) = edgeBC(MagneticPotential=0);
  end
end
% mesh sizes
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
    if dmin > 1e-9 * span, error('mm_ms_solve: no vertex at (%g, %g)', opts.hvertex(i,1), opts.hvertex(i,2)); end
    hv(end+1:end+2) = {vid, opts.hvertex(i,3)}; %#ok<AGROW>
  end
  args = [args, {'Hvertex', hv}];
end
m = generateMesh(m, args{:});
if isfield(opts, 'solver')
  fn = fieldnames(opts.solver);
  for i = 1:numel(fn), m.SolverOptions.(fn{i}) = opts.solver.(fn{i}); end
end
res = solve(m);
msh = m.Geometry.Mesh;
A = res.MagneticPotential(:);
if opts.axi
  r = msh.Nodes(1,:)';
  S.u = A .* r;              % the toolbox's own unknown u = r*A_phi (A set to 0 on the axis)
else
  S.u = A;
end
elemRegion = zeros(1, size(msh.Elements, 2));
for f = 1:gm.NumFaces
  elemRegion(findElements(msh, "region", Face=f)) = faceRegion(f);
end
S.msh = msh; S.faceRegion = faceRegion; S.edgeSide = edgeSide; S.elemRegion = elemRegion;
S.nodes = size(msh.Nodes, 2); S.elements = size(msh.Elements, 2);
S.result = res; S.model = m;
S.time = toc(t0);
end
