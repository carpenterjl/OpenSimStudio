function [u, info] = nl_newton_planar(msh, elemRegion, regions, C, pinned, opts)
%NL_NEWTON_PLANAR Nonlinear planar magnetostatics, -div(nu(|B|) grad A) = J,
% on a 6-node quadratic triangle mesh (from the PDE Toolbox), by Newton-Raphson
% with the exact tangent (differential reluctivity) and an Armijo line search
% on the convex energy functional Pi(A) = int w(|grad A|) dA - int J A dA,
% w(B) = int_0^B H dB. Regions: .kind 'core' uses the B-H curve C (bh_curve),
% others are linear with .mur; .J = current density. pinned: Dirichlet nodes (A = 0).
% opts.tol (relative residual, default 1e-10), opts.maxit (60), opts.qn (3).
if nargin < 6, opts = struct; end
if ~isfield(opts, 'tol'), opts.tol = 1e-10; end
if ~isfield(opts, 'maxit'), opts.maxit = 60; end
if ~isfield(opts, 'qn'), opts.qn = 3; end
mu0 = 4e-7*pi;
R = mm_trirule(opts.qn);
T = msh.Elements;
nE = size(T, 2); nN = size(msh.Nodes, 2); nq = numel(R.w);
X = msh.Nodes(1,:); Y = msh.Nodes(2,:);
x1 = X(T(1,:)); x2 = X(T(2,:)); x3 = X(T(3,:));
y1 = Y(T(1,:)); y2 = Y(T(2,:)); y3 = Y(T(3,:));
det = (x2 - x1) .* (y3 - y1) - (x3 - x1) .* (y2 - y1);
L2 = R.xi; L3 = R.eta; L1 = 1 - L2 - L3;
dXi  = [-(4*L1-1), (4*L2-1), zeros(nq,1), 4*(L1 - L2), 4*L3, -4*L3];
dEta = [-(4*L1-1), zeros(nq,1), (4*L3-1), -4*L2, 4*L2, 4*(L1 - L3)];
% NX{q}, NY{q}: 6 x nE shape-function gradients at quadrature point q; W: nq x nE
NX = cell(nq, 1); NY = cell(nq, 1);
for q = 1:nq
  NX{q} = (dXi(q,:)' * (y3 - y1) - dEta(q,:)' * (y2 - y1)) ./ det;
  NY{q} = (-dXi(q,:)' * (x3 - x1) + dEta(q,:)' * (x2 - x1)) ./ det;
end
W = R.w * abs(det);
kinds = {regions.kind};
isCore = false(1, nE);
nuLin = ones(1, nE) / mu0;
Jel = zeros(1, nE);
for k = 1:numel(regions)
  in = elemRegion == k;
  if strcmp(kinds{k}, 'core'), isCore(in) = true;
  else, nuLin(in) = 1 / (mu0 * regions(k).mur); end
  if isfield(regions, 'J') && ~isempty(regions(k).J), Jel(in) = regions(k).J; end
end
% load vector
f = zeros(nN, 1);
src = find(Jel ~= 0);
if ~isempty(src)
  for k = unique(Jel(src))
    el = find(Jel == k);
    f = f + mm_load(msh, el, @(x, y) k + 0*x, R);
  end
end
free = true(nN, 1); free(pinned) = false;
[II, JJ] = ndgrid(1:6, 1:6);
rowsI = T(II(:), :); colsJ = T(JJ(:), :);
% initial guess: linear solve with the initial reluctivity
info.resid = []; info.alpha = []; info.energy = []; info.step = [];
u = zeros(nN, 1);
[~, Kt] = evalSystem(u, true);
u(free) = Kt(free, free) \ f(free);
fnorm = norm(f(free));
for it = 1:opts.maxit
  [r, Kt, Pi0] = evalSystem(u, false);
  rn = norm(r(free)) / fnorm;
  info.resid(end+1) = rn; info.energy(end+1) = Pi0;
  if rn < opts.tol, break; end
  du = zeros(nN, 1);
  du(free) = -(Kt(free, free) \ r(free));
  stepRel = max(abs(du)) / max(abs(u));
  info.step(it) = stepRel;
  % converged in the solution: the Newton correction is at round-off level
  % (the residual itself floors near 1e-10 of the load in this high-contrast problem)
  if rn < 1e-7 && stepRel < 1e-11, break; end
  slope = r(free)' * du(free);
  alpha = 1;
  for ls = 1:30
    % Armijo on the energy functional; near convergence the energy change falls
    % below its round-off, so a step that lowers the residual norm is accepted too.
    [Pi1, rn1] = evalEnergy(u + alpha * du);
    if Pi1 <= Pi0 + 1e-4 * alpha * slope || rn1 < (1 - 1e-4 * alpha) * rn, break; end
    alpha = alpha / 2;
  end
  info.alpha(end+1) = alpha;
  u = u + alpha * du;
end
[r, ~, Pi0] = evalSystem(u, false);
info.final_resid = norm(r(free)) / fnorm;
info.iterations = it;
if isempty(info.step), info.final_step = NaN; else, info.final_step = info.step(end); end
info.energyFunctional = Pi0;
info.f = f;

  function [r, Kt, Pi] = evalSystem(uu, linearInit)
    U = uu(T);
    Kv = zeros(36, nE); rv = zeros(6, nE); Pi = 0;
    for qq = 1:nq
      ux = sum(NX{qq} .* U, 1); uy = sum(NY{qq} .* U, 1);
      B = sqrt(ux.^2 + uy.^2);
      nu = nuLin; dnu = zeros(1, nE); wd = 0.5 * nuLin .* B.^2;
      if linearInit
        nu(isCore) = C.nu0;
      else
        Bc = B(isCore);
        nu(isCore) = C.nu(Bc);
        dnu(isCore) = C.dH(Bc) - nu(isCore);   % nu_incremental - nu (0 at B = 0)
        wd(isCore) = C.w(Bc);
      end
      bx = zeros(1, nE); by = zeros(1, nE);
      nz = B > 0; bx(nz) = ux(nz) ./ B(nz); by(nz) = uy(nz) ./ B(nz);
      gB = NX{qq} .* bx + NY{qq} .* by;         % b_hat . grad N_i  (6 x nE)
      wq = W(qq, :);
      rv = rv + (NX{qq} .* (nu .* ux) + NY{qq} .* (nu .* uy)) .* wq;
      Kv = Kv + (NX{qq}(II(:), :) .* NX{qq}(JJ(:), :) + NY{qq}(II(:), :) .* NY{qq}(JJ(:), :)) .* (nu .* wq) ...
              + (gB(II(:), :) .* gB(JJ(:), :)) .* (dnu .* wq);
      Pi = Pi + sum(wd .* wq);
    end
    r = accumarray(T(:), rv(:), [nN, 1]) - f;
    Kt = sparse(rowsI(:), colsJ(:), Kv(:), nN, nN);
    Pi = Pi - f' * uu;
  end

  function [Pi, rnorm] = evalEnergy(uu)
    U = uu(T); Pi = 0; rv = zeros(6, nE);
    for qq = 1:nq
      ux = sum(NX{qq} .* U, 1); uy = sum(NY{qq} .* U, 1);
      B = sqrt(ux.^2 + uy.^2);
      wd = 0.5 * nuLin .* B.^2;
      nu = nuLin;
      wd(isCore) = C.w(B(isCore));
      nu(isCore) = C.nu(B(isCore));
      Pi = Pi + sum(wd .* W(qq, :));
      rv = rv + (NX{qq} .* (nu .* ux) + NY{qq} .* (nu .* uy)) .* W(qq, :);
    end
    Pi = Pi - f' * uu;
    rr = accumarray(T(:), rv(:), [nN, 1]) - f;
    rnorm = norm(rr(free)) / fnorm;
  end
end
