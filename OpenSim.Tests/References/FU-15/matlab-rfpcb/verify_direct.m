function verify_direct(kind, o, mel, fGHz, tag)
% verify_direct  Direct (no interpolation) MoM solves at chosen frequencies, one at a time,
% appending each result to results/<tag>.csv so partial results survive a time limit.
set(0,'DefaultFigureVisible','off');
switch kind
  case 'filter', p = build_filter(o);
  case 'patch',  p = build_patch(o);
end
md = mesh(p,'MaxEdgeLength',mel*1e-3); close all
fprintf('%s: mel %.3f mm, tri %d, tet %d\n', tag, mel, size(md.Triangles,2), size(md.Tetrahedra,2));
fn = fullfile('results',[tag '.csv']);
if ~isfile(fn)
  fid = fopen(fn,'w');
  if strcmp(kind,'filter')
    fprintf(fid,'freq_GHz,S11_re,S11_im,S21_re,S21_im,S12_re,S12_im,S22_re,S22_im,S11_dB,S21_dB\n');
  else
    fprintf(fid,'freq_GHz,S11_re,S11_im,S11_dB\n');
  end
  fclose(fid);
end
for fq = fGHz(:).'
  t0 = tic; s = sparameters(p, fq*1e9); S = s.Parameters;
  fid = fopen(fn,'a');
  if strcmp(kind,'filter')
    fprintf(fid,'%.6f,%.8f,%.8f,%.8f,%.8f,%.8f,%.8f,%.8f,%.8f,%.5f,%.5f\n', fq, real(S(1,1)), imag(S(1,1)), ...
      real(S(2,1)), imag(S(2,1)), real(S(1,2)), imag(S(1,2)), real(S(2,2)), imag(S(2,2)), ...
      20*log10(abs(S(1,1))), 20*log10(abs(S(2,1))));
    fprintf('%.4f GHz: S11 %.3f dB, S21 %.3f dB (%.1f s)\n', fq, 20*log10(abs(S(1,1))), 20*log10(abs(S(2,1))), toc(t0));
  else
    fprintf(fid,'%.6f,%.8f,%.8f,%.5f\n', fq, real(S(1,1)), imag(S(1,1)), 20*log10(abs(S(1,1))));
    fprintf('%.4f GHz: S11 %.3f dB (%.1f s)\n', fq, 20*log10(abs(S(1,1))), toc(t0));
  end
  fclose(fid);
end
end
