function diag_line(o, mels, fGHz, L1mm, L2mm, solverType, tag)
% diag_line  Mesh-convergence diagnostic of a MoM (or FEM) microstrip line: two thru lines of
% lengths L1, L2 (edge feeds at both board ends), direct solves, two-length extraction of eps_eff.
% o: struct for build_thru (Wf, er, h, BLx).  Appends rows to results/<tag>.csv.
set(0,'DefaultFigureVisible','off');
c0 = 299792458; L1 = L1mm*1e-3; L2 = L2mm*1e-3;
fn = fullfile('results',[tag '.csv']);
if ~isfile(fn)
  fid = fopen(fn,'w'); fprintf(fid,'solver,mel_mm,Wf_mm,er,h_mm,BLx_mm,freq_GHz,tet2,eps_eff,Z0_L1,Z0_L2,t_s\n'); fclose(fid);
end
for mel = mels
  t0 = tic;
  o1 = o; o1.Lt = L1mm; o2 = o; o2.Lt = L2mm;
  p1 = build_thru(o1); p2 = build_thru(o2);
  p1.SolverType = solverType; p2.SolverType = solverType;
  if ~isnan(mel)
    mesh(p1,'MaxEdgeLength',mel*1e-3); close all
    m2 = mesh(p2,'MaxEdgeLength',mel*1e-3); close all; nt = size(m2.Tetrahedra,2);
  else
    nt = -1;
  end
  s1 = sparameters(p1, fGHz*1e9); s2 = sparameters(p2, fGHz*1e9);
  for k = 1:numel(fGHz)
    T1 = s2abcd(s1.Parameters(:,:,k), 50); T2 = s2abcd(s2.Parameters(:,:,k), 50);
    M = T2 / T1;
    x = acosh(trace(M)/2); if imag(x) < 0, x = -x; end
    g = x / (L2 - L1);
    ee = (imag(g)*c0/(2*pi*fGHz(k)*1e9))^2;
    Z0a = (1/T1(2,1)) * sinh(g*L1); Z0b = (1/T2(2,1)) * sinh(g*L2);
    fid = fopen(fn,'a');
    fprintf(fid,'%s,%.3f,%.3f,%.3f,%.3f,%.2f,%.4f,%d,%.5f,%.3f,%.3f,%.1f\n', solverType, mel, o.Wf, o.er, o.h, o.BLx, fGHz(k), nt, ee, real(Z0a), real(Z0b), toc(t0));
    fclose(fid);
    fprintf('%s mel %.2f f %.3f: tet %d eps_eff %.4f Z0 %.2f/%.2f (%.0f s)\n', solverType, mel, fGHz(k), nt, ee, real(Z0a), real(Z0b), toc(t0));
  end
end
end
