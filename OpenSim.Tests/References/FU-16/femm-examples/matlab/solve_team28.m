function r = solve_team28(R, s, freq)
% SOLVE_TEAM28  FEMM team28a/k geometry, axisymmetric time-harmonic, psi = r*A_phi = 0 on the axis
% and on the box r <= R, |z| <= R (the A = 0 box that replaces FEMM's open boundary).
% Coils: stranded (uniform J, non-conducting), 960 and -576 turns, 20 A peak in series.
% Plate: sigma = 34 MS/m, free induced ring current. Mesh scale s. Weight 1/r at centroids.
if nargin < 3, freq = 50; end
tic;
mu0 = 4e-7 * pi; cm = 0.01; w = 2 * pi * freq; I = 20; sig = 34e6;
rect = @(x0, x1, y0, y1, h) struct('x', [x0 x1 x1 x0], 'y', [y0 y0 y1 y1], 'h', h);
Ci = [2.7 5.5 -2.6 2.6] * cm; Co = [8 9.5 -2.6 2.6] * cm; Pl = [0 6.5 3.73 4.03] * cm;
Ni = 960; No = -576;
sh = rect(0, R, -R, R, NaN);
sh(end + 1) = rect(0, 0.20, -0.12, 0.14, 4e-3 * s);
sh(end + 1) = rect(0, 0.11, -0.04, 0.06, 1.5e-3 * s);
sh(end + 1) = rect(Ci(1), Ci(2), Ci(3), Ci(4), 1.0e-3 * s);
sh(end + 1) = rect(Co(1), Co(2), Co(3), Co(4), 1.0e-3 * s);
sh(end + 1) = rect(0, 0.075, 0.033, 0.045, 0.5e-3 * s);
sh(end + 1) = rect(Pl(1), Pl(2), Pl(3), Pl(4), 0.25e-3 * s);
[p, t] = mesh_shapes(sh, R / 15, 1.3);
g = p1_geom(p, t);
inside = @(B) g.cx > B(1) & g.cx < B(2) & g.cy > B(3) & g.cy < B(4);
inCi = inside(Ci); inCo = inside(Co); inPl = inside(Pl);
Ai = (Ci(2) - Ci(1)) * (Ci(4) - Ci(3)); Ao = (Co(2) - Co(1)) * (Co(4) - Co(3));
rc = g.cx;
kcoef = 1 ./ (mu0 * rc);
mcoef = zeros(size(rc)); mcoef(inPl) = sig ./ rc(inPl);
J = zeros(size(rc)); J(inCi) = Ni * I / Ai; J(inCo) = No * I / Ao;
[K, M, F] = p1_assemble(g, kcoef, mcoef, J);
tol = 1e-12;
bnd = p(1, :) < tol | abs(p(1, :) - R) < tol * 1e3 | abs(p(2, :) + R) < tol * 1e3 | abs(p(2, :) - R) < tol * 1e3;
free = ~bnd;
u = zeros(g.np, 1);
S = K + 1i * w * M;
u(free) = S(free, free) \ F(free);
gr = sum(g.bx .* u(t), 1); gz = sum(g.by .* u(t), 1);
Br = -gz ./ rc; Bz = gr ./ rc;
% plate current density (nodal, element's centroid radius), loss and Lorentz force
Jn = -1i * w * sig * u(t) ./ rc;          % 3 x nt (only meaningful in the plate)
Jn(:, ~inPl) = 0;
vol = 2 * pi * rc .* g.area;
Jsq = (2 * sum(abs(Jn) .^ 2, 1) + 2 * real(Jn(1, :) .* conj(Jn(2, :)) + Jn(1, :) .* conj(Jn(3, :)) + Jn(2, :) .* conj(Jn(3, :)))) / 12;  % (1/A) int |J|^2 dA
Ploss = sum(0.5 / sig * Jsq(inPl) .* vol(inPl));
Javg = mean(Jn, 1);
Fz_lorentz = -0.5 * sum(real(Javg(inPl) .* conj(Br(inPl))) .* vol(inPl));
Ir = sum(Javg(inPl) .* g.area(inPl));     % total induced ring current (A, complex)
% weighted Maxwell stress over air bands around the plate
dx = max([Pl(1) - p(1, :); zeros(1, g.np); p(1, :) - Pl(2)], [], 1);
dy = max([Pl(3) - p(2, :); zeros(1, g.np); p(2, :) - Pl(4)], [], 1);
d = hypot(dx, dy);
deltas = [2e-3 5e-3]; Fz_wst = zeros(1, 2);
for kk = 1:2
    wt = max(0, 1 - d / deltas(kk));
    wx = sum(g.bx .* wt(t), 1); wy = sum(g.by .* wt(t), 1);
    act = (abs(wx) > 0 | abs(wy) > 0) & ~inPl;
    if any(act & (inCi | inCo)), error('stress band touches a coil'); end
    txx = 0.25 * (abs(Br) .^ 2 - abs(Bz) .^ 2) / mu0; txy = 0.5 * real(Br .* conj(Bz)) / mu0;
    Fz_wst(kk) = -sum((txy(act) .* wx(act) - txx(act) .* wy(act)) .* vol(act));
end
% coils: flux linkage of the series circuit, proximity loss (OpenSim's homogenised formula)
um = mean(u(t), 1);
lam = 2 * pi * (Ni / Ai * sum(g.area(inCi) .* um(inCi)) + No / Ao * sum(g.area(inCo) .* um(inCo)));
d18 = 1.0239652968433499e-3; sigcu = 58e6; aw = pi * d18 ^ 2 / 4;
fill_i = abs(Ni) * aw / Ai; fill_o = abs(No) * aw / Ao;
Bsq = abs(Br) .^ 2 + abs(Bz) .^ 2;
Pprox = sigcu * w ^ 2 * d18 ^ 2 / 32 * (fill_i * sum(Bsq(inCi) .* vol(inCi)) + fill_o * sum(Bsq(inCo) .* vol(inCo)));
Rdc = abs(Ni) * 2 * pi * mean(Ci(1:2)) / (sigcu * aw) + abs(No) * 2 * pi * mean(Co(1:2)) / (sigcu * aw);
r.R_box_m = R; r.scale = s; r.freq_Hz = freq; r.nodes = g.np; r.elements = numel(g.area);
r.Fz_lorentz_N = Fz_lorentz;
r.Fz_wst_band2mm_N = Fz_wst(1);
r.Fz_wst_band5mm_N = Fz_wst(2);
r.P_plate_W = Ploss;
r.I_plate_ring_A_re = real(Ir); r.I_plate_ring_A_im = imag(Ir);
r.flux_linkage_Wb_re = real(lam); r.flux_linkage_Wb_im = imag(lam);
r.Z_ohm_re_without_Rdc = real(1i * w * lam / I); r.Z_ohm_im = imag(1i * w * lam / I);
r.P_coil_dc_W = 0.5 * I ^ 2 * Rdc;
r.P_coil_proximity_W = Pprox;
r.plate_area_check_cm2 = sum(g.area(inPl)) / cm ^ 2;
r.seconds = toc;
end
