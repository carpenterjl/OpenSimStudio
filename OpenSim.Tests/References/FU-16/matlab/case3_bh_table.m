% case3_bh_table.m - write the gate's B-H table (82 points) and both interpolants
% sampled on a fine B grid (H, dH/dB, w = int H dB, mu_r), so the main session can
% check OpenSim's BhCurve against exactly what MATLAB used.
Cp = bh_curve('pchip'); Co = bh_curve('opensim');
T = Cp.table;
fid = fopen('bh_table.csv', 'w');
fprintf(fid, 'k,H_A_per_m,B_T\n');
for i = 1:size(T, 1)
  if i == 1, k = -1; else, k = i - 2; end
  fprintf(fid, '%d,%.17g,%.17g\n', k, T(i,1), T(i,2));
end
fclose(fid);
b = [linspace(0, 1.7, 69), Cp.Bmax, 1.75, 1.8, 2.0, 2.5];
fid = fopen('bh_interpolants.csv', 'w');
fprintf(fid, 'B_T,H_pchip,H_opensimFC,dHdB_pchip,dHdB_opensimFC,w_pchip_J_per_m3,w_opensimFC_J_per_m3,mur_pchip,mur_opensimFC\n');
for x = b
  fprintf(fid, '%.10g,%.12g,%.12g,%.12g,%.12g,%.12g,%.12g,%.12g,%.12g\n', x, Cp.H(x), Co.H(x), Cp.dH(x), Co.dH(x), Cp.w(x), Co.w(x), Cp.mur(x), Co.mur(x));
end
fclose(fid);
fprintf('table: %d points, H %g..%g A/m, B %.12f..%.12f T\n', size(T,1), T(1,1), T(end,1), T(1,2), T(end,2));
fprintf('initial mu_r: pchip %.6f, opensim-FC %.6f (analytic curve: %.6f)\n', Cp.mur(0), Co.mur(0), 1 + (2000-1));
% the interpolants' largest departure from the analytic curve between the knots
mu0 = 4e-7*pi; Js = 1.6; muri = 2000;
Hs = logspace(0, 5, 20001);
Bs = mu0*Hs + (2*Js/pi)*atan(pi*(muri - 1)*mu0*Hs/(2*Js));
ep = max(abs(Cp.H(Bs) - Hs) ./ Hs); eo = max(abs(Co.H(Bs) - Hs) ./ Hs);
fprintf('max |H_interp(B(H)) - H|/H over 1..1e5 A/m: pchip %.3e, opensim-FC %.3e\n', ep, eo);
