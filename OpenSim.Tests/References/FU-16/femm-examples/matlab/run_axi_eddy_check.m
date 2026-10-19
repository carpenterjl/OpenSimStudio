% Validation of the axisymmetric eddy-current assembly used for TEAM 28: a solid aluminium
% cylinder (radius a, sigma 34 MS/m, 50 Hz) in a uniform axial field, psi = r*A_phi prescribed
% on r = a, natural (zero normal derivative) on z = 0 and z = h, psi = 0 on the axis.
% Exact: B_z = C I0(alpha r), psi(r) = C r I1(alpha r)/alpha, J_phi = -(C alpha/mu0) I1(alpha r).
mu0 = 4e-7 * pi; sig = 34e6; f = 50; w = 2 * pi * f; a = 0.065; h = 0.02; Psi_a = 1e-4;
alpha = sqrt(1i * w * mu0 * sig);
C = Psi_a * alpha / (a * besseli(1, alpha * a));
Pex = 0.5 / sig * integral(@(r) abs(C * alpha / mu0 * besseli(1, alpha * r)) .^ 2 * 2 * pi .* r, 0, a, 'RelTol', 1e-12) * h;
Iex = integral(@(r) -C * alpha / mu0 * besseli(1, alpha * r), 0, a, 'RelTol', 1e-12) * h;
res = [];
for s = [2 1 0.5 0.25]
    rect = @(x0, x1, y0, y1, hh) struct('x', [x0 x1 x1 x0], 'y', [y0 y0 y1 y1], 'h', hh);
    sh = rect(0, a, 0, h, 1e-3 * s);
    [p, t] = mesh_shapes(sh, 1e-3 * s, 1.3);
    g = p1_geom(p, t);
    rc = g.cx;
    [K, M, F] = p1_assemble(g, 1 ./ (mu0 * rc), sig ./ rc, zeros(size(rc)));
    axis_ = p(1, :) < 1e-12; outer = abs(p(1, :) - a) < 1e-12;
    u = zeros(g.np, 1); u(outer) = Psi_a;
    free = ~(axis_ | outer);
    S = K + 1i * w * M;
    u(free) = S(free, free) \ (F(free) - S(free, ~free) * u(~free));
    Jn = -1i * w * sig * u(t) ./ rc;
    Jsq = (2 * sum(abs(Jn) .^ 2, 1) + 2 * real(Jn(1, :) .* conj(Jn(2, :)) + Jn(1, :) .* conj(Jn(3, :)) + Jn(2, :) .* conj(Jn(3, :)))) / 12;
    P = sum(0.5 / sig * Jsq .* 2 * pi .* rc .* g.area);
    Itot = sum(mean(Jn, 1) .* g.area);
    r.scale = s; r.nodes = g.np; r.P_fe_W = P; r.P_exact_W = Pex; r.P_rel_err = P / Pex - 1;
    r.I_fe_re = real(Itot); r.I_fe_im = imag(Itot); r.I_exact_re = real(Iex); r.I_exact_im = imag(Iex);
    r.I_rel_err = abs(Itot - Iex) / abs(Iex);
    disp(r); res = [res, r]; %#ok<AGROW>
end
fid = fopen('axi_eddy_check.json', 'w'); fprintf(fid, '%s', jsonencode(res, 'PrettyPrint', true)); fclose(fid);
