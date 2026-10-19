function r = solve_tube(Lbox_in, Npoly, s)
% SOLVE_TUBE  FEMM tube.fem as OpenSim would model it: planar, 1 Hz, A = 0 on a square box
% |x|,|y| <= Lbox (instead of FEMM's A = 0 on the r = 1 in circle), circles as inscribed N-gons
% (FEMM's own 5 deg arcs are 72-gons). Wire: solid conductor, 100 A; tube: solid conductor, 0 A
% (zero net current). J = sigma (g - j w A) with one g per conductor, as MagneticSolver2D.
tic;
mu0 = 4e-7 * pi; in = 0.0254; f = 1; w = 2 * pi * f;
ri = 0.5 * in; ro = 1.0 * in; rw = 0.1 * in; L = Lbox_in * in;
mut = 1000; sigt = 10e6; sigw = 58e6;
th = (0:Npoly - 1) * 2 * pi / Npoly;
ngon = @(rr) struct('x', rr * cos(th), 'y', rr * sin(th));
sh = struct('x', [-L L L -L], 'y', [-L -L L L], 'h', NaN);
q = ngon(ro); q.h = 0.5e-3 * s; sh(end + 1) = q;
q = ngon(ri); q.h = 0.5e-3 * s; sh(end + 1) = q;
q = ngon(rw); q.h = 0.25e-3 * s; sh(end + 1) = q;
[p, t] = mesh_shapes(sh, L / 10, 1.3);
g = p1_geom(p, t);
inO = inpolygon(g.cx, g.cy, ro * cos(th), ro * sin(th));
inI = inpolygon(g.cx, g.cy, ri * cos(th), ri * sin(th));
inW = inpolygon(g.cx, g.cy, rw * cos(th), rw * sin(th));
inT = inO & ~inI;
nu = ones(size(g.cx)) / mu0; nu(inT) = 1 / (mut * mu0);
sig = zeros(size(g.cx)); sig(inT) = sigt; sig(inW) = sigw;
[K, M, ~] = p1_assemble(g, nu, sig, zeros(size(g.cx)));
np = g.np;
% conductor vectors b_k = int sigma N_i, S_k = int sigma
bW = accumarray(reshape(t(:, inW), [], 1), reshape(repmat(sig(inW) .* g.area(inW) / 3, 3, 1), [], 1), [np 1]);
bT = accumarray(reshape(t(:, inT), [], 1), reshape(repmat(sig(inT) .* g.area(inT) / 3, 3, 1), [], 1), [np 1]);
SW = sum(sig(inW) .* g.area(inW)); ST = sum(sig(inT) .* g.area(inT));
tol = 1e-12;
bnd = abs(abs(p(1, :)) - L) < tol | abs(abs(p(2, :)) - L) < tol;
free = find(~bnd);
nf = numel(free);
Sys = [K(free, free) + 1i * w * M(free, free), -bW(free), -bT(free);
       -1i * w * bW(free).', SW, 0;
       -1i * w * bT(free).', 0, ST];
rhs = [zeros(nf, 1); 100; 0];
x = Sys \ rhs;
A = zeros(np, 1); A(free) = x(1:nf); gW = x(nf + 1); gT = x(nf + 2);
gg = zeros(size(g.cx)); gg(inW) = gW; gg(inT) = gT;
Jn = sig .* (gg - 1i * w * A(t));    % 3 x nt
Jsq = (2 * sum(abs(Jn) .^ 2, 1) + 2 * real(Jn(1, :) .* conj(Jn(2, :)) + Jn(1, :) .* conj(Jn(3, :)) + Jn(2, :) .* conj(Jn(3, :)))) / 12;
Pt = sum(0.5 ./ sigt .* Jsq(inT) .* g.area(inT));
Pw = sum(0.5 ./ sigw .* Jsq(inW) .* g.area(inW));
It = sum(mean(Jn(:, inT), 1) .* g.area(inT));
gx = sum(g.bx .* A(t), 1); gy = sum(g.by .* A(t), 1);
Bx = gy; By = -gx;
rs = [0.55 0.6 0.65 0.7 0.75 0.8 0.85 0.9 0.95] * in;
Bs = zeros(size(rs));
for k = 1:numel(rs)
    % average over the elements whose centroid lies within a small ring segment around (r, 0)
    sel = abs(hypot(g.cx, g.cy) - rs(k)) < 0.25e-3 & abs(atan2(g.cy, g.cx)) < 0.05;
    Bphi = -Bx(sel) .* sin(atan2(g.cy(sel), g.cx(sel))) + By(sel) .* cos(atan2(g.cy(sel), g.cx(sel)));
    Bs(k) = mean(Bphi);
end
r.Lbox_in = Lbox_in; r.Npoly = Npoly; r.scale = s; r.nodes = np; r.elements = numel(g.area);
r.P_tube_W_per_m = Pt; r.P_wire_W_per_m = Pw;
r.I_tube_net_A_abs = abs(It);
r.r_in = rs / in; r.Bphi_re_T = real(Bs); r.Bphi_im_T = imag(Bs);
r.seconds = toc;
end
