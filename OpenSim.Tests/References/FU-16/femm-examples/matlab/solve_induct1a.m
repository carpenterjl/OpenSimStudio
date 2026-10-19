function r = solve_induct1a(s, dg_in)
% SOLVE_INDUCT1A  FEMM induct1a.fem (magnetostatic, planar, A = 0 on the 2.5 x 2 in box) on a
% mesh scaled by s, with the I-section lowered by dg_in inches (gap = 0.025 + dg_in).
% Returns L, W and the weighted-stress force on the I-section per FEMM's 1 in depth.
tic;
mu0 = 4e-7 * pi; in = 0.0254; depth = 1 * in; N = 66; Icoil = 1;
gap = (0.025 + dg_in) * in;
Ex = [-0.75 -0.5 -0.5 -0.25 -0.25 0.25 0.25 0.5 0.5 0.75 0.75 -0.75] * in;
Ey = [0 0 0.5 0.5 0 0 0.5 0.5 0 0 0.75 0.75] * in;
rect = @(x0, x1, y0, y1, h) struct('x', [x0 x1 x1 x0], 'y', [y0 y0 y1 y1], 'h', h);
I_r = [-0.75 * in, 0.75 * in, -gap - 0.25 * in, -gap];
SL = [-0.5 -0.25 0 0.5] * in; SR = [0.25 0.5 0 0.5] * in;
Aslot = (SL(2) - SL(1)) * (SL(4) - SL(3));
sh = rect(-1.25 * in, 1.25 * in, -0.75 * in, 1.25 * in, NaN);
sh(end + 1) = rect(-1.0 * in, 1.0 * in, -0.5 * in, 1.0 * in, 0.05 * in * s);
sh(end + 1) = struct('x', Ex, 'y', Ey, 'h', 0.025 * in * s);
sh(end + 1) = rect(I_r(1), I_r(2), I_r(3), I_r(4), 0.025 * in * s);
sh(end + 1) = rect(SL(1), SL(2), SL(3), SL(4), 0.025 * in * s);
sh(end + 1) = rect(SR(1), SR(2), SR(3), SR(4), 0.025 * in * s);
sh(end + 1) = rect(-0.75 * in, 0.75 * in, -gap, 0, 0.025 / 4 * in * s);
[p, t] = mesh_shapes(sh, 0.1 * in, 1.25);
g = p1_geom(p, t);
inE = inpolygon(g.cx, g.cy, Ex, Ey);
inI = g.cx > I_r(1) & g.cx < I_r(2) & g.cy > I_r(3) & g.cy < I_r(4);
inL = g.cx > SL(1) & g.cx < SL(2) & g.cy > SL(3) & g.cy < SL(4);
inR = g.cx > SR(1) & g.cx < SR(2) & g.cy > SR(3) & g.cy < SR(4);
nu = ones(1, numel(g.area)) / mu0; nu(inE | inI) = 1 / (2500 * mu0);
J = zeros(1, numel(g.area)); J(inL) = N * Icoil / Aslot; J(inR) = -N * Icoil / Aslot;
[K, ~, F] = p1_assemble(g, nu, zeros(size(nu)), J);
tol = 1e-9;
bnd = abs(p(1, :) + 1.25 * in) < tol | abs(p(1, :) - 1.25 * in) < tol | abs(p(2, :) + 0.75 * in) < tol | abs(p(2, :) - 1.25 * in) < tol;
free = ~bnd;
A = zeros(g.np, 1);
A(free) = K(free, free) \ F(free);
Am = mean(A(t), 1);
lam = N / Aslot * (sum(g.area(inL) .* Am(inL)) - sum(g.area(inR) .* Am(inR)));
gx = sum(g.bx .* A(t), 1); gy = sum(g.by .* A(t), 1);
Bx = gy; By = -gx;
W = 0.5 * sum(nu .* (Bx .^ 2 + By .^ 2) .* g.area);
deltas = [0.0125 0.02] * in; Fy = zeros(1, 2); Fx = zeros(1, 2);
dx = max([I_r(1) - p(1, :); zeros(1, g.np); p(1, :) - I_r(2)], [], 1);
dy = max([I_r(3) - p(2, :); zeros(1, g.np); p(2, :) - I_r(4)], [], 1);
d = hypot(dx, dy);
for kk = 1:2
    w = max(0, 1 - d / deltas(kk));
    wx = sum(g.bx .* w(t), 1); wy = sum(g.by .* w(t), 1);
    act = (abs(wx) > 0 | abs(wy) > 0) & ~inI;
    if any(act & (inE | inL | inR)), error('stress band touches iron or coil'); end
    txx = 0.5 * (Bx .^ 2 - By .^ 2) / mu0; txy = Bx .* By / mu0;
    Fx(kk) = -sum((txx(act) .* wx(act) + txy(act) .* wy(act)) .* g.area(act));
    Fy(kk) = -sum((txy(act) .* wx(act) - txx(act) .* wy(act)) .* g.area(act));
end
% gap-centre flux density under the centre pole
sel = abs(g.cy + gap / 2) < gap / 2 & abs(g.cx) < 0.05 * in;
r.scale = s; r.dg_in = dg_in; r.nodes = g.np; r.elements = numel(g.area);
r.L_H_per_1in = lam * depth / Icoil;
r.W_J_per_1in = W * depth;
r.L_from_W_H_per_1in = 2 * W * depth / Icoil ^ 2;
r.Fy_WST_band0p0125in_N_per_1in = Fy(1) * depth;
r.Fy_WST_band0p02in_N_per_1in = Fy(2) * depth;
r.Fx_WST_band0p02in_N_per_1in = Fx(2) * depth;
r.By_gap_centre_T = mean(By(sel));
r.seconds = toc;
end
