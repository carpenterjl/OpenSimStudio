% txline_check.m - closed-form (RF Toolbox txlineMicrostrip) Z0 and eps_eff of the lines,
% for comparison with the thesis values (filter: 50.77 ohm feed, 49.05 ohm sections, eps_eff 1.895,
% p.108-109; patch feed: nominally 50 ohm, eps_eff 2.14, p.158).  Zero-thickness, lossless.
% Z0 and eps_eff are taken from the ABCD matrix of a 10 mm section: cosh(gL) = A, Z0 = sqrt(B/C).
c0 = 299792458; L = 0.01;
fid = fopen(fullfile('results','txline_check.txt'),'w');
cases = {'filter line 2.413 mm, er 2.2, h 0.794 mm', 2.413e-3, 0.794e-3, 2.2, [1e9 2e9 5e9];
         'filter patch 2.54 mm, er 2.2, h 0.794 mm', 2.54e-3, 0.794e-3, 2.2, [1e9 2e9 5e9];
         'patch feed 4.48 mm, er 2.55, h 1.59 mm',    4.48e-3, 1.59e-3, 2.55, [1e9 2.28e9]};
for k = 1:size(cases,1)
  t = txlineMicrostrip('Width',cases{k,2},'Height',cases{k,3},'Thickness',0,'EpsilonR',cases{k,4}, ...
                       'LossTangent',0,'SigmaCond',Inf,'LineLength',L);
  for fq = cases{k,5}
    s = sparameters(t, fq);
    T = s2abcd(s.Parameters, 50);
    g = acosh(T(1,1)) / L;
    z0 = sqrt(T(1,2)/T(2,1));
    ee = (imag(g) * c0 / (2*pi*fq))^2;
    msg = sprintf('%s @ %.2f GHz: Z0 = %.3f ohm, eps_eff = %.4f (txlineMicrostrip closed form)', cases{k,1}, fq/1e9, real(z0), ee);
    fprintf(fid, '%s\n', msg); disp(msg)
  end
end
fclose(fid);
