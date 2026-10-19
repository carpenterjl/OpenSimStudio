% run_wire.m - validation of stripRac on a round copper wire against the
% exact internal impedance Z/R_dc = (ka/2) J0(ka)/J1(ka), k = (1-1j)/delta.
sigma = 5.8e7; mu0 = 4e-7*pi; a = 0.5e-3;
ratios = [1 2 4 8];
hfs = [4 2 1 1/2 1/4 1/8];
fid = fopen('wire_results.csv', 'w');
fprintf(fid, ['case,a_m,sigma,f_Hz,a_over_delta,mesh_level,hmaxFactor,hs_m,hs_over_delta,Hgrad,RfarFactor,quarter,nodes,' ...
    'Rac_over_Rdc_FE,Rac_over_Rdc_powerform,Rac_over_Rdc_exact,rel_err,Lint_FE_Hpm,Lint_exact_Hpm,dL_FE_Hpm,dL_exact_Hpm,areaRelErr,t_s\n']);
fprintf('exact values (double besselj vs 40-digit vpa):\n');
for r = ratios
    f = r^2/(pi*mu0*sigma*a^2);
    k = (1-1j)*r/a;
    zeta = (k*a/2)*besselj(0, k*a)/besselj(1, k*a);
    zs = (sym(1-1j)*sym(r)/2)*besselj(0, sym(1-1j)*sym(r))/besselj(1, sym(1-1j)*sym(r));
    zv = double(vpa(zs, 40));
    fprintf('  a/delta=%g f=%.6g Hz  Re=%.12f (vpa %.12f)  Im=%.12f (vpa %.12f)\n', r, f, real(zeta), real(zv), imag(zeta), imag(zv));
end
row = @(fid, tag, r, f, lev, hf, info, ra, ex, LiEx, dLEx, Li) fprintf(fid, ...
    '%s,%.6g,%.6g,%.10g,%g,%d,%.6g,%.6g,%.6g,%g,%g,%d,%d,%.10f,%.10f,%.10f,%.4e,%.8e,%.8e,%.8e,%.8e,%.3e,%.2f\n', ...
    tag, a, sigma, f, r, lev, hf, info.hs_m, info.hs_m/info.delta, info.hgrad, info.RfarFactor, info.quarter, info.nodes, ...
    ra, info.ratioPower, ex, ra/ex - 1, Li, LiEx, info.dL, dLEx, info.areaRelErr, info.tMesh + info.tSolve + info.tDc);
for r = ratios
    f = r^2/(pi*mu0*sigma*a^2);
    k = (1-1j)*r/a;
    zeta = (k*a/2)*besselj(0, k*a)/besselj(1, k*a);
    ex = real(zeta);
    for hf = hfs
        lev = round(1 - log2(hf));
        [ra, Li, info] = stripRac(2*a, 2*a, sigma, f, hf, 'Shape', 'round');
        LiEx = mu0*imag(zeta)/(pi*info.kappa);
        dLEx = LiEx - mu0/(8*pi);
        fprintf('a/d=%g level %d (hs=%.3g delta) nodes %7d  R/Rdc FE %.9f power %.9f exact %.9f relerr %+.3e | Lint relerr %+.3e dL relerr %+.3e\n', ...
            r, lev, info.hs_m/info.delta, info.nodes, ra, info.ratioPower, ex, ra/ex-1, Li/LiEx-1, info.dL/dLEx-1);
        row(fid, 'round', r, f, lev, hf, info, ra, ex, LiEx, dLEx, Li);
    end
    % far boundary doubled at level 3 (hf = 1/4)
    [ra, Li, info] = stripRac(2*a, 2*a, sigma, f, 1/4, 'Shape', 'round', 'RfarFactor', 40);
    LiEx = mu0*imag(zeta)/(pi*info.kappa); dLEx = LiEx - mu0/(8*pi);
    fprintf('a/d=%g level 3 Rfar x2: R/Rdc %.9f relerr %+.3e  dL %.6e (exact %.6e)\n', r, ra, ra/ex-1, info.dL, dLEx);
    row(fid, 'round_Rfar40', r, f, 3, 1/4, info, ra, ex, LiEx, dLEx, Li);
    % full (non-symmetric) model at level 3
    [ra, Li, info] = stripRac(2*a, 2*a, sigma, f, 1/4, 'Shape', 'round', 'Quarter', false);
    fprintf('a/d=%g level 3 full model: R/Rdc %.9f relerr %+.3e nodes %d\n', r, ra, ra/ex-1, info.nodes);
    row(fid, 'round_full', r, f, 3, 1/4, info, ra, ex, LiEx, dLEx, Li);
end
% Hgrad sensitivity at a/delta = 8, level 1
r = 8; f = r^2/(pi*mu0*sigma*a^2); k = (1-1j)*r/a;
ex = real((k*a/2)*besselj(0, k*a)/besselj(1, k*a));
for hg = [1.15 1.5]
    [ra, ~, info] = stripRac(2*a, 2*a, sigma, f, 1, 'Shape', 'round', 'Hgrad', hg);
    fprintf('a/d=8 level 1 Hgrad %.2f: R/Rdc %.9f relerr %+.3e nodes %d\n', hg, ra, ra/ex-1, info.nodes);
end
fclose(fid);
