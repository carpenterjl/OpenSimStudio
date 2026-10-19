% FU-13: the ideal-rectangle R_ac/R_dc at the points read off Haefner (1937), Fig. 8.
% Haefner's similitude variable p = sqrt(2*mu0*f/R'_dc), R'_dc = 1/(sigma*w*t) per metre, so
% f = p^2 * R'_dc / (2*mu0); shape = w/t (his a/c). Thickness 1 mm, sigma 5.8e7 (the ratio
% depends on shape and p only). Three mesh levels each; DC solve skipped (ratio only).
cases = [1 4.0; 1 6.5; 2 6.0; 6 6.5; 12 8.0; 12 10.0];
sigma = 5.8e7; mu0 = 4e-7*pi; t = 1e-3;
fid = fopen('haefner_fem.csv', 'w');
fprintf(fid, 'shape,p,f_Hz,level,Rac_over_Rdc\n');
for i = 1:size(cases, 1)
    w = cases(i, 1) * t; p = cases(i, 2);
    f = p^2 / (sigma * w * t) / (2 * mu0);
    for L = 1:3
        r = stripRac(w, t, sigma, f, 2^(1 - L), 'DcSolve', false);
        fprintf(fid, '%g,%g,%.9g,%d,%.9f\n', cases(i, 1), p, f, L, r);
        fprintf('shape %g p %g level %d: %.9f\n', cases(i, 1), p, L, r);
    end
end
fclose(fid);
