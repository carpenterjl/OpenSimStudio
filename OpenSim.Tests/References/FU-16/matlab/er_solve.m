function out = er_solve(NI, lev, variant, G)
%ER_SOLVE One nonlinear solve of the ER leg section at ampere-turns NI on mesh
% level lev (f = 2^(lev-1): hmax 3 mm/f, core and windows 0.5 mm/f, the eight
% re-entrant window corners 0.05 mm/f, Hgrad 1.3) with the B-H interpolant
% variant ('pchip' or 'opensim'). Pass G (from a previous call) to reuse the mesh.
if nargin < 3, variant = 'pchip'; end
mu0 = 4e-7*pi;
C = bh_curve(variant);
M = er_model(NI, 1);
for k = M.core, M.regions(k).mur = NaN; end
if nargin < 4 || isempty(G)
  f = 2^(lev - 1);
  hreg = nan(1, numel(M.regions));
  hreg(M.core) = 0.5e-3 / f; hreg([M.winP M.winM]) = 0.5e-3 / f;
  G = mm_mesh(M.regions, M.domain, struct('hmax', 3e-3 / f, 'hregion', hreg, ...
    'hvertex', [M.windowCorners, repmat(0.05e-3/f, 8, 1)], 'hgrad', 1.3));
end
pinned = unique(findNodes(G.msh, 'region', 'Edge', find(G.edgeSide ~= ' ')));
tt = tic;
[u, info] = nl_newton_planar(G.msh, G.elemRegion, M.regions, C, pinned, struct('tol', 1e-10, 'maxit', 60));
R = mm_trirule(4);
% flux linkage per metre of the window coil (1 turn, current NI)
lam = 0;
for k = [M.winP M.winM]
  Q = mm_quad(G.msh, find(G.elemRegion == k), u, R);
  lam = lam + sign(M.regions(k).J) * sum(Q.w(:) .* Q.u(:)) / sum(Q.w(:));
end
Q = mm_quad(G.msh, 1:G.elements, u, R);
B = hypot(Q.ux, Q.uy);
isCore = ismember(G.elemRegion, M.core);
wd = 0.5 * B.^2 / mu0;
Bc = B(:, isCore);
wd(:, isCore) = C.w(Bc);
W = sum(sum(Q.w .* wd));
% coenergy for reference: int (B*H - w) over the core + same as energy in air
Hc = C.H(Bc);
cod = 0.5 * B.^2 / mu0; cod(:, isCore) = Bc .* Hc - C.w(Bc);
Wco = sum(sum(Q.w .* cod));
% peak |B| in the core, all points and away from the re-entrant corners
xc = Q.x(:, isCore); yc = Q.y(:, isCore);
dmin = inf(size(xc));
for i = 1:size(M.windowCorners, 1)
  dmin = min(dmin, hypot(xc - M.windowCorners(i,1), yc - M.windowCorners(i,2)));
end
Wc = Q.w(:, isCore);
out.areaAboveTable = sum(Wc(Bc > C.Bmax));     % core area [m^2] where |B| exceeds the table's last point
out.Bmax = max(Bc(:));
out.Bmax_excl05 = max(Bc(dmin > 0.5e-3));
out.Bmax_excl10 = max(Bc(dmin > 1.0e-3));
% mean B across the right leg and the post at mid-height (flux per metre / width)
A = mm_pointval(G.msh, u, [M.rw 0; M.outer 0; -M.post 0; M.post 0]);
out.Bleg = abs(A(1) - A(2)) / (M.outer - M.rw);
out.Bpost = abs(A(3) - A(4)) / (2*M.post);
out.lambda = lam; out.W = W; out.Wco = Wco; out.NI = NI;
out.iterations = info.iterations; out.resid = info.final_resid; out.step = info.final_step;
out.residHistory = info.resid;
out.nodes = G.nodes; out.time = toc(tt); out.G = G; out.u = u;
out.tableBmax = C.Bmax;
end
