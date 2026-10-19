function case3_femodel_xcheck(NIs, lev, tol)
%CASE3_FEMODEL_XCHECK femodel's own nonlinear magnetostatic solve of the ER
% section (materialProperties(RelativePermeability = @(loc,state) mu_r(|B|))),
% started from the Newton solution through faceIC(MagneticVectorPotential=...)
% on the same mesh. femodel turns the IC into a linear solve at mu_r(B_IC) (a
% Picard step) and then runs its Gauss-Newton until its residual tolerance
% (default 1e-4); lambda' and W' of its result are compared with the Newton ones.
% Appends to case3_femodel_xcheck.csv.
if nargin < 3, tol = []; end
mu0 = 4e-7*pi;
C = bh_curve('pchip');
fn = 'case3_femodel_xcheck.csv';
newFile = ~isfile(fn);
fid = fopen(fn, 'a');
if newFile, fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n'); end
R = mm_trirule(4);
G = [];
for NI = NIs
  o = er_solve(NI, lev, 'pchip', G);
  G = o.G;
  M = er_model(NI, 1);
  m = G.model;
  murfun = @(loc, st) C.mur(st.NormFluxDensity);
  icfun = @(loc) reshape(mm_pointval(G.msh, o.u, [loc.x(:), loc.y(:)]), size(loc.x));
  for fc = 1:numel(G.faceRegion)
    k = G.faceRegion(fc);
    if k == 0 || ~strcmp(M.regions(k).kind, 'core')
      m.MaterialProperties(fc) = materialProperties(RelativePermeability=1);
    else
      m.MaterialProperties(fc) = materialProperties(RelativePermeability=murfun);
    end
    if k > 0 && M.regions(k).J ~= 0, m.FaceLoad(fc) = faceLoad(CurrentDensity=M.regions(k).J); end
    m.FaceIC(fc) = faceIC(MagneticVectorPotential=icfun);
  end
  for e = find(G.edgeSide ~= ' '), m.EdgeBC(e) = edgeBC(MagneticPotential=0); end
  m.SolverOptions.ReportStatistics = 'on';
  m.SolverOptions.MaxIterations = 30;
  if ~isempty(tol), m.SolverOptions.ResidualTolerance = tol; end
  tolUsed = m.SolverOptions.ResidualTolerance;
  tt = tic;
  try
    res = solve(m);
    u = res.MagneticPotential(:);
    ok = true;
  catch err
    fprintf('femodel failed at NI %g: %s\n', NI, err.message);
    ok = false;
  end
  if ok
    lam = 0;
    for k = [M.winP M.winM]
      Q = mm_quad(G.msh, find(G.elemRegion == k), u, R);
      lam = lam + sign(M.regions(k).J) * sum(Q.w(:) .* Q.u(:)) / sum(Q.w(:));
    end
    Q = mm_quad(G.msh, 1:G.elements, u, R);
    B = hypot(Q.ux, Q.uy);
    isCore = ismember(G.elemRegion, M.core);
    wd = 0.5 * B.^2 / mu0; wd(:, isCore) = C.w(B(:, isCore));
    W = sum(sum(Q.w .* wd));
    du = max(abs(u - o.u)) / max(abs(o.u));
    fprintf('NI %g level %d nodes %d: femodel lambda %.9e vs Newton %.9e (%.2e); W %.9e vs %.9e (%.2e); max|dA|/max|A| %.2e (%.1f s)\n', ...
      NI, lev, G.nodes, lam, o.lambda, lam/o.lambda - 1, W, o.W, W/o.W - 1, du, toc(tt));
    p = sprintf('NI=%gA tol=%g', NI, tolUsed);
    mm_csv(fid, 'case3_femodel_xcheck', p, 'lambda_femodel_Wb_per_m', lam, lev, G.nodes);
    mm_csv(fid, 'case3_femodel_xcheck', p, 'lambda_rel_diff_vs_newton', lam/o.lambda - 1, lev, G.nodes);
    mm_csv(fid, 'case3_femodel_xcheck', p, 'W_rel_diff_vs_newton', W/o.W - 1, lev, G.nodes);
    mm_csv(fid, 'case3_femodel_xcheck', p, 'max_rel_diff_A', du, lev, G.nodes);
  else
    mm_csv(fid, 'case3_femodel_xcheck', sprintf('NI=%gA tol=%g', NI, tolUsed), 'failed_too_many_iterations', 1, lev, G.nodes);
  end
end
fclose(fid);
end
