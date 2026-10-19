% Rautio 1994 stripline standard: independent check with Symbolic Math Toolbox vpa (50 digits).
% Paper p. 2047: Z0*sqrt(er) = eta0/4 * K(k')/K(k), k = tanh(pi/2 * w/b), k' = sqrt(1-k^2), eta0 = 376.7303136.
% MATLAB ellipticK(m) takes the PARAMETER m = k^2.
digits(50);
eta0 = vpa('376.7303136');
b = vpa(1); er = vpa(1);
Zfun = @(w) eta0/4 * ellipticK(1 - tanh(vpa(pi)/2*w/b)^2) / ellipticK(tanh(vpa(pi)/2*w/b)^2) / sqrt(er);
% Cohn's form, written independently: k = sech, k' = tanh, K(k)/K(k')
Zcohn = @(w) eta0/(4*sqrt(er)) * ellipticK(sech(vpa(pi)*w/(2*b))^2) / ellipticK(tanh(vpa(pi)*w/(2*b))^2);
Wp = [vpa('3.3260319'), vpa('1.4423896'), vpa('0.50396767')];
Zt = [vpa(25), vpa(50), vpa(100)];
fid = fopen('rautio_vpa_results.csv','w');
fprintf(fid, 'Z0_target,W_printed_mm,Z0_at_W_printed_paperform,Z0_at_W_printed_cohnform,W_exact_mm\n');
for i = 1:3
    z1 = Zfun(Wp(i)); z2 = Zcohn(Wp(i));
    syms w
    wx = vpasolve(Zfun(w) == Zt(i), w, [vpa('0.05') vpa('10')]);
    fprintf('Z0 target %s | W printed %s | Z0(Wp) paper form %s | Cohn form %s | W exact %s\n', ...
        char(Zt(i)), char(Wp(i)), char(vpa(z1,30)), char(vpa(z2,30)), char(vpa(wx,30)));
    fprintf(fid, '%s,%s,%s,%s,%s\n', char(Zt(i)), char(Wp(i)), char(vpa(z1,30)), char(vpa(z2,30)), char(vpa(wx,30)));
end
fclose(fid);
c0 = vpa(299792458); f = vpa(15e9);
Lq = c0/(4*f)*1000;
fprintf('quarter wave at 15 GHz [mm] = %s\n', char(vpa(Lq,30)));
fprintf('electrical length of 4.99654097 mm [deg] = %s\n', char(vpa(360*vpa('4.99654097')/1000*f/c0, 30)));
fprintf('120*pi/eta0 - 1 = %s\n', char(vpa(120*vpa(pi)/eta0 - 1, 20)));
fprintf('4*pi*1e-7*c = %s\n', char(vpa(4*vpa(pi)*vpa('1e-7')*c0, 30)));
% repo cross-check: w = 0.5 mm, b = 1.6 mm, er = 4.4, eta0 = 4*pi*1e-7*c
etaR = 4*vpa(pi)*vpa('1e-7')*c0; bR = vpa('1.6'); wR = vpa('0.5'); erR = vpa('4.4');
kR = tanh(vpa(pi)*wR/(2*bR));
zR = etaR/4*ellipticK(1-kR^2)/ellipticK(kR^2)/sqrt(erR);
fprintf('repo case w0.5 b1.6 er4.4 Z0 = %s\n', char(vpa(zR, 20)));
