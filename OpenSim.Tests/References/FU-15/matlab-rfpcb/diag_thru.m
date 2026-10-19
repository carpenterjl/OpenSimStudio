function diag_thru(mels, fGHz, BLx, tag)
% diag_thru  Mesh-convergence diagnostic of the MoM microstrip line used to feed the patch:
% two thru lines (16.75 and 33.5 mm) per mesh size, direct solves, two-length extraction of
% eps_eff and of Z0 (series-port model).  Appends rows to results/<tag>.csv.
set(0,'DefaultFigureVisible','off');
c0 = 299792458; L1 = 16.75e-3; L2 = 33.5e-3;
fn = fullfile('results',[tag '.csv']);
if ~isfile(fn)
  fid = fopen(fn,'w'); fprintf(fid,'mel_mm,BLx_mm,freq_GHz,tri1,tet1,tri2,tet2,eps_eff,alpha,Z0_L1,Z0_L2,Xs_L1,Xs_L2,S11_L1_dB,S11_L2_dB,t_s\n'); fclose(fid);
end
for mel = mels
  t0 = tic;
  p1 = build_thru(struct('Lt',16.75,'BLx',BLx)); m1 = mesh(p1,'MaxEdgeLength',mel*1e-3); close all
  p2 = build_thru(struct('Lt',33.5,'BLx',BLx));  m2 = mesh(p2,'MaxEdgeLength',mel*1e-3); close all
  s1 = sparameters(p1, fGHz*1e9); s2 = sparameters(p2, fGHz*1e9);
  for k = 1:numel(fGHz)
    T1 = s2abcd(s1.Parameters(:,:,k), 50); T2 = s2abcd(s2.Parameters(:,:,k), 50);
    M = T2 / T1;
    x = acosh(trace(M)/2); if imag(x) < 0, x = -x; end
    g = x / (L2 - L1);
    ee = (imag(g)*c0/(2*pi*fGHz(k)*1e9))^2;
    Z0a = (1/T1(2,1)) * sinh(g*L1); Zsa = T1(1,1)/T1(2,1) - Z0a*coth(g*L1);
    Z0b = (1/T2(2,1)) * sinh(g*L2); Zsb = T2(1,1)/T2(2,1) - Z0b*coth(g*L2);
    fid = fopen(fn,'a');
    fprintf(fid,'%.3f,%.2f,%.4f,%d,%d,%d,%d,%.5f,%.5f,%.3f,%.3f,%.3f,%.3f,%.2f,%.2f,%.1f\n', mel, BLx, fGHz(k), ...
      size(m1.Triangles,2), size(m1.Tetrahedra,2), size(m2.Triangles,2), size(m2.Tetrahedra,2), ee, real(g), ...
      real(Z0a), real(Z0b), imag(Zsa), imag(Zsb), 20*log10(abs(s1.Parameters(1,1,k))), 20*log10(abs(s2.Parameters(1,1,k))), toc(t0));
    fclose(fid);
    fprintf('mel %.2f BLx %.1f f %.3f: tet %d/%d eps_eff %.4f Z0 %.2f/%.2f Xs %.2f/%.2f (%.0f s)\n', mel, BLx, fGHz(k), ...
      size(m1.Tetrahedra,2), size(m2.Tetrahedra,2), ee, real(Z0a), real(Z0b), imag(Zsa), imag(Zsb), toc(t0));
  end
end
end
