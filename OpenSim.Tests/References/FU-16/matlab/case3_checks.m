% case3_checks.m - checks of the nonlinear Newton solver (nl_newton_planar)
%  (1) linear limit: the ER leg section with a linear core mu_r 2000 solved by
%      nl_newton_planar and by femodel on the same mesh: lambda' and W' agree.
%  (2) the ParallelPlates line (MagneticsTests.cs lines 23-37) with the gap
%      filled by the gate's B-H material at H = I/W = 3000 and 50000 A/m:
%      1-D, so H_gap = I/W exactly, B_gap = H^-1(I/W) of the interpolant,
%      lambda' = B_gap*s + mu0*H*(2t/3), W' = w(B_gap)*s*W + mu0*H^2*t*W/3.
%  (3) femodel's own nonlinear solver (numerical-Jacobian Gauss-Newton) tried on (2).
mu0 = 4e-7*pi;
fid = fopen('case3_checks.csv', 'w');
fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n');
R = mm_trirule(4);
%% (1)
NI = 20;
M = er_model(NI, 2000);
hreg = nan(1, numel(M.regions)); hreg(M.core) = 1e-3; hreg([M.winP M.winM]) = 1e-3;
G = mm_mesh(M.regions, M.domain, struct('hmax', 4e-3, 'hregion', hreg, 'hvertex', [M.windowCorners, repmat(0.1e-3, 8, 1)], 'hgrad', 1.3));
pinned = unique(findNodes(G.msh, 'region', 'Edge', find(G.edgeSide ~= ' ')));
nuc = 1/(mu0*2000);
Clin = struct('nu0', nuc, 'nu', @(b) nuc + 0*b, 'dH', @(b) nuc + 0*b, 'w', @(b) 0.5*nuc*b.^2);
[u1, info1] = nl_newton_planar(G.msh, G.elemRegion, M.regions, Clin, pinned, struct('tol', 1e-10));
m = G.model;
for fc = 1:numel(G.faceRegion)
  k = G.faceRegion(fc);
  if k == 0, m.MaterialProperties(fc) = materialProperties(RelativePermeability=1);
  else
    m.MaterialProperties(fc) = materialProperties(RelativePermeability=M.regions(k).mur);
    if M.regions(k).J ~= 0, m.FaceLoad(fc) = faceLoad(CurrentDensity=M.regions(k).J); end
  end
end
for e = find(G.edgeSide ~= ' '), m.EdgeBC(e) = edgeBC(MagneticPotential=0); end
res = solve(m);
u2 = res.MagneticPotential(:);
out = zeros(2, 2);
for s = 1:2
  if s == 1, u = u1; else, u = u2; end
  lam = 0;
  for k = [M.winP M.winM]
    Q = mm_quad(G.msh, find(G.elemRegion == k), u, R);
    lam = lam + sign(M.regions(k).J) * sum(Q.w(:) .* Q.u(:)) / sum(Q.w(:));
  end
  Q = mm_quad(G.msh, 1:G.elements, u, R);
  nuE = ones(1, G.elements)/mu0; nuE(ismember(G.elemRegion, M.core)) = nuc;
  Wm = 0.5 * sum(sum(Q.w .* nuE .* (Q.ux.^2 + Q.uy.^2)));
  out(s, :) = [lam, Wm];
end
fprintf('(1) linear ER section NI %g, nodes %d: Newton lambda %.10e W %.10e (%d it, resid %.1e); femodel lambda %.10e W %.10e; rel diff %.2e / %.2e; max|du|/max|u| %.2e\n', ...
  NI, G.nodes, out(1,1), out(1,2), info1.iterations, info1.final_resid, out(2,1), out(2,2), out(1,1)/out(2,1) - 1, out(1,2)/out(2,2) - 1, max(abs(u1 - u2))/max(abs(u2)));
mm_csv(fid, 'check1_linear_vs_femodel', sprintf('NI=%g mur=2000', NI), 'lambda_rel_diff', out(1,1)/out(2,1) - 1, 1, G.nodes);
mm_csv(fid, 'check1_linear_vs_femodel', sprintf('NI=%g mur=2000', NI), 'W_rel_diff', out(1,2)/out(2,2) - 1, 1, G.nodes);
mm_csv(fid, 'check1_linear_vs_femodel', sprintf('NI=%g mur=2000', NI), 'max_rel_diff_A', max(abs(u1 - u2))/max(abs(u2)), 1, G.nodes);
%% (2)
s = 2e-3; t = 0.5e-3; Wd = 10e-3;
C = bh_curve('pchip');
for H = [3000 50000]
  I = H * Wd;
  reg = struct('name', {'go','return','gap'}, 'x0', {-s/2 - t, s/2, -s/2}, 'x1', {-s/2, s/2 + t, s/2}, ...
    'y0', {0, 0, 0}, 'y1', {Wd, Wd, Wd}, 'mur', {1, 1, 1}, 'J', {I/(t*Wd), -I/(t*Wd), 0}, 'kind', {'air','air','core'});
  G = mm_mesh(reg, [-5e-3 5e-3 0 Wd], struct('hmax', 0.25e-3, 'hgrad', 1.3));
  pinned = unique(findNodes(G.msh, 'region', 'Edge', find(G.edgeSide == 'L')));
  [u, info] = nl_newton_planar(G.msh, G.elemRegion, reg, C, pinned, struct('tol', 1e-10));
  Bex = fzero(@(b) C.H(b) - H, [0, 3]);
  lamEx = Bex*s + mu0*H*2*t/3;
  WEx = C.w(Bex)*s*Wd + mu0*H^2*t*Wd/3;
  lam = 0;
  for k = 1:2
    Q = mm_quad(G.msh, find(G.elemRegion == k), u, R);
    lam = lam + sign(reg(k).J) * sum(Q.w(:) .* Q.u(:)) / sum(Q.w(:));
  end
  Q = mm_quad(G.msh, 1:G.elements, u, R);
  B = hypot(Q.ux, Q.uy);
  ing = G.elemRegion == 3;
  wd = 0.5 * B.^2 / mu0; wd(:, ing) = C.w(B(:, ing));
  Wm = sum(sum(Q.w .* wd));
  Bg = B(:, ing);
  Bana = mu0*H + (2*1.6/pi)*atan(pi*1999*mu0*H/(2*1.6));
  fprintf(['(2) H %g A/m: B_gap FE %.10f..%.10f T, interpolant %.10f T (analytic curve %.10f); lambda %.10e vs %.10e (%.1e); ' ...
    'W %.10e vs %.10e (%.1e); Newton %d it, resid %s\n'], H, min(Bg(:)), max(Bg(:)), Bex, Bana, lam, lamEx, lam/lamEx - 1, Wm, WEx, Wm/WEx - 1, ...
    info.iterations, mat2str(info.resid, 3));
  p = sprintf('H=%gA/m', H);
  mm_csv(fid, 'check2_nonlinear_plates', p, 'B_gap_max_rel_err', max(abs(Bg(:)/Bex - 1)), 1, G.nodes);
  mm_csv(fid, 'check2_nonlinear_plates', p, 'lambda_rel_err', lam/lamEx - 1, 1, G.nodes);
  mm_csv(fid, 'check2_nonlinear_plates', p, 'W_rel_err', Wm/WEx - 1, 1, G.nodes);
  mm_csv(fid, 'check2_nonlinear_plates', p, 'B_interpolant_minus_analytic_T', Bex - Bana, '-', 0);
  mm_csv(fid, 'check2_nonlinear_plates', p, 'newton_iterations', info.iterations, 1, G.nodes);
  %% (3) femodel's own nonlinear solver on the same problem
  try
    reg2 = reg; reg2(3).mur = @(loc, st) C.mur(st.NormFluxDensity);
    tt = tic;
    S = mm_ms_solve(reg2, [-5e-3 5e-3 0 Wd], struct('axi', false, 'dirichlet', 'L', 'hmax', 0.25e-3, 'hgrad', 1.3, ...
      'solver', struct('ResidualTolerance', 1e-10, 'MaxIterations', 40)));
    Q = mm_quad(S.msh, find(S.elemRegion == 3), S.u, R);
    Bf = hypot(Q.ux, Q.uy);
    fprintf('(3) femodel nonlinear: B_gap %.10f..%.10f T (%.1f s)\n', min(Bf(:)), max(Bf(:)), toc(tt));
    mm_csv(fid, 'check3_femodel_nonlinear_plates', p, 'B_gap_max_rel_err', max(abs(Bf(:)/Bex - 1)), 1, S.nodes);
  catch err
    fprintf('(3) femodel nonlinear failed: %s\n', err.message);
    mm_csv(fid, 'check3_femodel_nonlinear_plates', p, 'failed', 1, 1, 0);
  end
end
fclose(fid);
