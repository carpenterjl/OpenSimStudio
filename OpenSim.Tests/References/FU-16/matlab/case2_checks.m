% case2_checks.m - checks of the psi = r*A_phi time-harmonic formulation used in case 2.
%  (a) DC resistance of a solid copper ring at 1 Hz: R = 2*pi/(sigma*c*ln(r1/r0))
%      (the geometry of MagneticsTests.SolidRing_AtLowFrequency_HasItsDcResistance,
%      lines 170-187: r0 5 mm, r1 6 mm, c 1 mm, domain r 0..0.05, z +-0.05 m).
%  (b) mu jump across a constant-r interface: an infinitely long solenoid (sheet
%      current J in a 5..5.5 mm shell, natural BC top/bottom/right) around a
%      mu_r = 2000 core r < 3 mm: H_z is piecewise known, psi(r) = int_0^r mu H_z r dr.
%  (c) eddy currents (the a term): a copper cylinder r < 3 mm inside the same
%      solenoid, delta = 1 mm: H_z = K*I0(kr)/I0(kb), psi(b) = mu0*K*b*I1(kb)/(k*I0(kb)).
%  (d) the pre-assembled j*omega route vs solvepde with complex a and f on one mesh.
mu0 = 4e-7*pi; sig = 5.8e7;
R = mm_trirule(4);
fid = fopen('case2_checks.csv', 'w');
fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n');
%% (a) DC ring
r0 = 5e-3; r1 = 6e-3; c = 1e-3;
reg = struct('name', 'ring', 'x0', r0, 'x1', r1, 'y0', -c/2, 'y1', c/2, 'mur', 1, 'sigma', sig);
Rdc = 2*pi / (sig * c * log(r1/r0));
mm_csv(fid, 'check_a_ring_dc', 'analytic', 'R_ohm', Rdc, '-', 0);
for lev = 1:2
  f = 2^(lev-1);
  S = mm_th_setup(reg, [0 0.05 -0.05 0.05], struct('dirichlet', 'TBRL', 'hmax', 5e-3/f, 'hregion', 0.1e-3/f, 'hgrad', 1.3));
  el = find(S.elemRegion == 1);
  F = mm_load(S.msh, el, @(x,y) sig/(2*pi) ./ x, R);
  w = 2*pi*1;
  U = mm_th_solve(S, w, F);
  Q = mm_quad(S.msh, el, U, R);
  I = sum(sum(Q.w .* (sig ./ Q.x) .* (1/(2*pi) - 1i*w*Q.u)));
  Z = 1/I;
  fprintf('(a) level %d nodes %d: R %.10e ohm, analytic %.10e, rel %.2e; X/omega %.6e H\n', lev, S.nodes, real(Z), Rdc, real(Z)/Rdc - 1, imag(Z)/w);
  mm_csv(fid, 'check_a_ring_dc', 'f=1Hz', 'R_ohm', real(Z), lev, S.nodes);
  mm_csv(fid, 'check_a_ring_dc', 'f=1Hz', 'R_rel_err', real(Z)/Rdc - 1, lev, S.nodes);
end
%% (b) mu jump, (c) eddy cylinder
b = 3e-3; a = 5e-3; t = 0.5e-3; h = 2e-3; J = 1e6; K = J*t;
for test = 'bc'
  if test == 'b', murc = 2000; sigc = 0; w = 2*pi*1e3; else, murc = 1; sigc = sig; delta = 1e-3; w = 2/(mu0*sig*delta^2); end
  reg = struct('name', {'core','shell'}, 'x0', {0, a}, 'x1', {b, a + t}, 'y0', {0, 0}, 'y1', {h, h}, ...
    'mur', {murc, 1}, 'sigma', {sigc, 0});
  for lev = 1:2
    f = 2^(lev-1);
    S = mm_th_setup(reg, [0 10e-3 0 h], struct('dirichlet', 'L', 'hmax', 0.25e-3/f, 'hregion', [0.1e-3/f, 0.05e-3/f], 'hgrad', 1.3));
    F = mm_load(S.msh, find(S.elemRegion == 2), @(x,y) J + 0*x, R);
    U = mm_th_solve(S, w, F);
    rr = S.msh.Nodes(1,:)'; u = U(:,1);
    if test == 'b'
      mu = mu0*murc;
      ex = zeros(size(rr));
      in = rr <= b; ex(in) = mu*K*rr(in).^2/2;
      g = rr > b & rr <= a; ex(g) = mu*K*b^2/2 + mu0*K*(rr(g).^2 - b^2)/2;
      s = rr > a & rr <= a + t; rs = rr(s);
      ex(s) = mu*K*b^2/2 + mu0*K*(a^2 - b^2)/2 + mu0*J*((a+t)*(rs.^2 - a^2)/2 - (rs.^3 - a^3)/3);
      o = rr > a + t; ex(o) = mu*K*b^2/2 + mu0*K*(a^2 - b^2)/2 + mu0*J*((a+t)*((a+t)^2 - a^2)/2 - ((a+t)^3 - a^3)/3);
      err = max(abs(u - ex)) / max(abs(ex));
      psib = mu*K*b^2/2;
      [~, ib] = min(hypot(rr - b, S.msh.Nodes(2,:)' - h/2));
      fprintf('(b) level %d nodes %d: max |psi - exact| / max|psi| = %.3e; psi(b) %.10e vs %.10e\n', lev, S.nodes, err, real(u(ib)), psib);
      mm_csv(fid, 'check_b_mu_jump', 'mur=2000 core', 'max_rel_err_psi', err, lev, S.nodes);
      mm_csv(fid, 'check_b_mu_jump', 'mur=2000 core', 'psi_b_rel_err', real(u(ib))/psib - 1, lev, S.nodes);
    else
      k = sqrt(1i*w*mu0*sig);
      psib = mu0*K*b*besseli(1, k*b) / (k*besseli(0, k*b));
      [~, ib] = min(hypot(rr - b, S.msh.Nodes(2,:)' - h/2));
      inn = rr <= b;
      ex = mu0*K*rr(inn).*besseli(1, k*rr(inn)) ./ (k*besseli(0, k*b));
      err = max(abs(u(inn) - ex)) / max(abs(ex));
      fprintf('(c) level %d nodes %d: psi(b) %.8e%+.8ei vs %.8e%+.8ei, rel %.2e; max err in cylinder %.2e\n', lev, S.nodes, ...
        real(u(ib)), imag(u(ib)), real(psib), imag(psib), abs(u(ib)/psib - 1), err);
      mm_csv(fid, 'check_c_eddy_cylinder', 'delta=1mm b=3mm', 'psi_b_rel_err', abs(u(ib)/psib - 1), lev, S.nodes);
      mm_csv(fid, 'check_c_eddy_cylinder', 'delta=1mm b=3mm', 'max_rel_err_psi_in_cylinder', err, lev, S.nodes);
    end
  end
end
%% (d) same model through solvepde with complex coefficients
r0 = 5e-3; r1 = 6e-3; c = 1e-3;
reg = struct('name', 'ring', 'x0', r0, 'x1', r1, 'y0', -c/2, 'y1', c/2, 'mur', 1, 'sigma', sig);
S = mm_th_setup(reg, [0 0.05 -0.05 0.05], struct('dirichlet', 'TBRL', 'hmax', 5e-3, 'hregion', 0.1e-3, 'hgrad', 1.3));
w = 2*pi*100e3;
el = find(S.elemRegion == 1);
F = mm_load(S.msh, el, @(x,y) sig/(2*pi) ./ x, R);
U1 = mm_th_solve(S, w, F);
model = S.model;
for fc = 1:numel(S.faceRegion)
  if S.faceRegion(fc) == 1
    specifyCoefficients(model, 'm', 0, 'd', 0, 'c', @(rg,st) (1/mu0)./rg.x, 'a', @(rg,st) 1i*w*sig./rg.x, ...
      'f', @(rg,st) sig/(2*pi)./rg.x, 'Face', fc);
  end
end
sol = solvepde(model);
U2 = sol.NodalSolution;
d = max(abs(U1 - U2)) / max(abs(U2));
Q1 = mm_quad(S.msh, el, U1, R); Q2 = mm_quad(S.msh, el, U2, R);
I1 = sum(sum(Q1.w .* (sig ./ Q1.x) .* (1/(2*pi) - 1i*w*Q1.u)));
I2 = sum(sum(Q2.w .* (sig ./ Q2.x) .* (1/(2*pi) - 1i*w*Q2.u)));
fprintf('(d) solvepde vs assembled j*omega route at 100 kHz: max |dpsi|/max|psi| = %.3e; Z %.8e%+.8ei vs %.8e%+.8ei\n', d, ...
  real(1/I1), imag(1/I1), real(1/I2), imag(1/I2));
mm_csv(fid, 'check_d_solvepde', 'f=100kHz ring', 'max_rel_diff_psi', d, 1, S.nodes);
mm_csv(fid, 'check_d_solvepde', 'f=100kHz ring', 'Z_rel_diff', abs(I2/I1 - 1), 1, S.nodes);
fclose(fid);
