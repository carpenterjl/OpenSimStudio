function r = solve_aircore(n, ri_in, ro_in, z_in, mbox, s, hmaxdiv, hgrad, nearmult)
% SOLVE_AIRCORE  FEMM AirCore coil (n turns, ri..ro, axial length z, inches; 1 A), axisymmetric
% magnetostatic, psi = 0 on the axis and on the box r <= R, |z| <= R with R = mbox * (FEMM's
% sphere radius 2*max(ro, ri, z)). Mesh scale s. Returns L = flux linkage / I.
tic;
if nargin < 7, hmaxdiv = 15; end
if nargin < 8, hgrad = 1.3; end
if nargin < 9, nearmult = 1.5; end
mu0 = 4e-7 * pi; in = 0.0254; I = 1;
ri = ri_in * in; ro = ro_in * in; zl = z_in * in;
rs = 2 * max([ro_in, ri_in, z_in]) * in;
R = mbox * rs;
D = max(ro, zl);
rect = @(x0, x1, y0, y1, h) struct('x', [x0 x1 x1 x0], 'y', [y0 y0 y1 y1], 'h', h);
C = [ri ro -zl / 2 zl / 2];
sh = rect(0, R, -R, R, NaN);
if nearmult * D < R
    sh(end + 1) = rect(0, nearmult * D, -nearmult * D, nearmult * D, D / 30 * s);
end
sh(end + 1) = rect(C(1), C(2), C(3), C(4), min(ro - ri, zl) / 12 * s);
[p, t] = mesh_shapes(sh, R / hmaxdiv, hgrad);
g = p1_geom(p, t);
inC = g.cx > C(1) & g.cx < C(2) & g.cy > C(3) & g.cy < C(4);
Ac = (ro - ri) * zl;
rc = g.cx;
J = zeros(size(rc)); J(inC) = n * I / Ac;
[K, ~, F] = p1_assemble(g, 1 ./ (mu0 * rc), zeros(size(rc)), J);
tol = 1e-12;
bnd = p(1, :) < tol | abs(p(1, :) - R) < 1e-9 | abs(p(2, :) + R) < 1e-9 | abs(p(2, :) - R) < 1e-9;
free = ~bnd;
u = zeros(g.np, 1);
u(free) = K(free, free) \ F(free);
um = mean(u(t), 1);
lam = 2 * pi * n / Ac * sum(g.area(inC) .* um(inC));
r.hmaxdiv = hmaxdiv; r.hgrad = hgrad; r.nearmult = nearmult;
r.n = n; r.ri_in = ri_in; r.ro_in = ro_in; r.z_in = z_in; r.mbox = mbox; r.R_box_m = R; r.scale = s;
r.nodes = g.np; r.elements = numel(g.area);
r.L_H = lam / I;
r.W_J = 0.5 * F' * u;    % = L I^2 / 2 (check)
r.seconds = toc;
end
