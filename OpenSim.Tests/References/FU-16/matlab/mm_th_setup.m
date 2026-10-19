function S = mm_th_setup(regions, domain, opts)
%MM_TH_SETUP Axisymmetric time-harmonic model in psi = r*A_phi with the
% general scalar PDE (createpde / specifyCoefficients):
%     -div( c grad psi ) + a psi = f,   c = 1/(mu r),  a = j*omega*sigma/r,
%     f = sigma*V/(2*pi*r) in a voltage-driven solid ring (or J for a stranded one).
% The PDE Toolbox assembles K (the c term) and A for a = sigma/r (real); the
% j*omega factor is applied by the caller, so one assembly serves every
% frequency (identical to specifying a = j*omega*sigma/r; mm_th_check compares
% the two on one mesh with solvepde).
%  regions: x0,x1,y0,y1, mur, sigma; opts: dirichlet sides ('L' = the axis),
%  hmax, hregion, hvertex [x y h], hgrad.
t0 = tic;
mu0 = 4e-7*pi;
if ~isfield(opts, 'hgrad'), opts.hgrad = 1.3; end
dl = mm_geom(regions, domain);
model = createpde(1);
geometryFromEdges(model, dl);
gm = model.Geometry;
span = max(domain(2) - domain(1), domain(4) - domain(3));
generateMesh(model, 'Hmax', span/8, 'GeometricOrder', 'linear');
[faceRegion, edgeSide] = mm_classify(model.Mesh, gm.NumFaces, gm.NumEdges, regions, domain);
for f = 1:gm.NumFaces
  k = faceRegion(f);
  if k == 0, mur = 1; sigma = 0; else, mur = regions(k).mur; sigma = regions(k).sigma; end
  cf = @(reg, st) (1/(mu0*mur)) ./ reg.x;
  if sigma > 0, af = @(reg, st) sigma ./ reg.x; else, af = 0; end
  specifyCoefficients(model, 'm', 0, 'd', 0, 'c', cf, 'a', af, 'f', 0, 'Face', f);
end
dEdges = find(ismember(edgeSide, opts.dirichlet));
applyBoundaryCondition(model, 'dirichlet', 'Edge', dEdges, 'u', 0);
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
    if dmin > 1e-9 * span, error('mm_th_setup: no vertex at (%g, %g)', opts.hvertex(i,1), opts.hvertex(i,2)); end
    hv(end+1:end+2) = {vid, opts.hvertex(i,3)}; %#ok<AGROW>
  end
  args = [args, {'Hvertex', hv}];
end
generateMesh(model, args{:});
msh = model.Mesh;
FEM = assembleFEMatrices(model);
S.K = FEM.K; S.A = FEM.A;
pinned = unique(findNodes(msh, 'region', 'Edge', dEdges));
free = true(size(msh.Nodes, 2), 1); free(pinned) = false;
S.free = free;
elemRegion = zeros(1, size(msh.Elements, 2));
for f = 1:gm.NumFaces
  elemRegion(findElements(msh, 'region', 'Face', f)) = faceRegion(f);
end
S.model = model; S.msh = msh; S.faceRegion = faceRegion; S.edgeSide = edgeSide;
S.elemRegion = elemRegion; S.nodes = size(msh.Nodes, 2); S.elements = size(msh.Elements, 2);
S.regions = regions; S.mu0 = mu0;
S.time = toc(t0);
end
