function case2_potcore_ac(config, levels, freqs)
%CASE2_POTCORE_AC Time-harmonic pot core winding, solid copper turns
% (PlanarWindingSection.Build with solidTurns = true; each turn a SolidConductor,
% all four in series carrying 1 A peak). config = 'core' (gap 0.2 mm, ferrite
% mu_r 2000, sigma 0) or 'air' (no core: Build's own smaller box).
% psi = r*A_phi; c = 1/(mu r), a = j*omega*sigma/r, f = sigma*V/(2*pi*r) in the
% driven turn. One solve per turn with V = 1 (the other turns shorted, V = 0,
% their induced current free) gives column k of the 4x4 admittance Y; the
% series winding with 1 A in every turn has Z = 1' * inv(Y) * 1.
% Writes case2_potcore_ac_<config>.csv.
if nargin < 2, levels = 1:3; end
if nargin < 3, freqs = [1 100e3 1e6]; end
mu0 = 4e-7*pi; sig = 5.8e7; gap = 0.2e-3;
withCore = strcmp(config, 'core');
M = potcore_model(gap, withCore, 2000, 0);
reg = M.regions;
for k = 1:numel(reg)
  if strcmp(reg(k).kind, 'turn'), reg(k).sigma = sig; else, reg(k).sigma = 0; end
end
turns = find(strcmp({reg.kind}, 'turn'));
t = M.Z1 - M.Z0;
Rdc = sum(2*pi ./ (sig * t * log(M.R1 ./ M.R0)));
deltas = 1 ./ sqrt(pi * freqs * mu0 * sig);
fn = sprintf('case2_potcore_ac_%s.csv', config);
newFile = ~isfile(fn);
fid = fopen(fn, 'a');
if newFile
  fprintf(fid, 'case,parameter,quantity,value,mesh_level,nodes\n');
  mm_csv(fid, ['case2_potcore_ac_' config], 'analytic', 'R_dc_series_ohm', Rdc, '-', 0);
  for i = 1:numel(freqs)
    mm_csv(fid, ['case2_potcore_ac_' config], sprintf('f=%gHz', freqs(i)), 'skin_depth_m', deltas(i), '-', 0);
  end
end
fprintf('%s: R_dc of the 4 turns in series %.8e ohm; skin depths %s um\n', config, Rdc, mat2str(deltas*1e6, 5));
R = mm_trirule(4);
for lev = levels
  f = 2^(lev - 1);
  hreg = nan(1, numel(reg));
  hreg(turns) = 16.5e-6 / f;              % <= delta/4 at 1 MHz (66 um) and 100 kHz
  hreg(strcmp({reg.kind}, 'box')) = 0.1e-3 / f;   % window air (core) / winding surroundings (air)
  if withCore
    hreg(strcmp({reg.kind}, 'core')) = 0.5e-3 / f;
    hreg(strcmp({reg.kind}, 'gap')) = gap / 4 / f;
    hv = [M.corners(1:2,:), repmat(gap/16/f, 2, 1); M.corners(3:4,:), repmat(gap/4/f, 2, 1); ...
          M.windowCorners, repmat(0.1e-3/f, 4, 1)];
  else
    hv = [];
  end
  opts = struct('dirichlet', 'TBRL', 'hmax', 4e-3 / f, 'hregion', hreg, 'hvertex', hv, 'hgrad', 1.3);
  S = mm_th_setup(reg, M.domain, opts);
  F = zeros(S.nodes, 4);
  for i = 1:4
    F(:, i) = mm_load(S.msh, find(S.elemRegion == turns(i)), @(x,y) sig/(2*pi) ./ x, R);
  end
  nuE = ones(1, S.elements) / mu0;
  nuE(ismember(S.elemRegion, find(strcmp({reg.kind}, 'core')))) = 1 / (mu0 * 2000);
  Iturn = zeros(1, 4);
  for fi = 1:numel(freqs)
    w = 2*pi*freqs(fi);
    tt = tic;
    U = mm_th_solve(S, w, F);
    Y = zeros(4);
    for i = 1:4
      el = find(S.elemRegion == turns(i));
      for k = 1:4
        Q = mm_quad(S.msh, el, U(:, k), R);
        Y(i, k) = sum(sum(Q.w .* (sig ./ Q.x) .* ((i == k)/(2*pi) - 1i*w*Q.u)));
      end
    end
    Z = inv(Y);
    Zs = sum(Z(:));
    % series solution: turn voltages Vs (1 A in each), field psi_s
    Vs = Z * ones(4, 1);
    psis = U * Vs;
    P = 0;
    for i = 1:4
      Q = mm_quad(S.msh, find(S.elemRegion == turns(i)), psis, R);
      Jq = (sig ./ Q.x) .* (Vs(i)/(2*pi) - 1i*w*Q.u);
      P = P + 0.5 * sum(sum(Q.w .* abs(Jq).^2 / sig .* (2*pi*Q.x)));
      Iturn(i) = sum(sum(Q.w .* Jq));
    end
    Q = mm_quad(S.msh, 1:S.elements, psis, R);
    Wavg = (pi/2) * sum(sum(Q.w .* nuE .* (abs(Q.ux).^2 + abs(Q.uy).^2) ./ Q.x));
    Rw = real(Zs); Lw = imag(Zs) / w;
    asym = norm(Y - Y.', 'fro') / norm(Y, 'fro');
    fprintf('%s f=%g Hz level %d nodes %d: R %.8e ohm (2P %.8e), L %.8e H (4W %.8e), |Y-Y''|/|Y| %.1e, turn currents %s (%.1f s solve; setup %.1f s)\n', ...
      config, freqs(fi), lev, S.nodes, Rw, 2*P, Lw, 4*Wavg, asym, mat2str(abs(Iturn), 6), toc(tt), S.time);
    p = sprintf('f=%gHz', freqs(fi));
    cname = ['case2_potcore_ac_' config];
    mm_csv(fid, cname, p, 'R_ohm', Rw, lev, S.nodes);
    mm_csv(fid, cname, p, 'L_H', Lw, lev, S.nodes);
    mm_csv(fid, cname, p, 'R_from_loss_ohm', 2*P, lev, S.nodes);
    mm_csv(fid, cname, p, 'L_from_energy_H', 4*Wavg, lev, S.nodes);
    mm_csv(fid, cname, p, 'R_over_Rdc', Rw / Rdc, lev, S.nodes);
    mm_csv(fid, cname, p, 'Y_asymmetry', asym, lev, S.nodes);
    for i = 1:4
      mm_csv(fid, cname, p, sprintf('Re_V_turn%d_V', i), real(Vs(i)), lev, S.nodes);
      mm_csv(fid, cname, p, sprintf('Im_V_turn%d_V', i), imag(Vs(i)), lev, S.nodes);
    end
  end
end
fclose(fid);
end
