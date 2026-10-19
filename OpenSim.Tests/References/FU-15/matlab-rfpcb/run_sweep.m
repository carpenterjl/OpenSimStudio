function run_sweep(kind, o, mel, fGHz, tag, sweepOpt)
% run_sweep  Mesh a structure at MaxEdgeLength = mel (mm), compute S-parameters
% (RF PCB Toolbox, SolverType 'MoM', 50 ohm reference) at the frequencies fGHz,
% and save results/<tag>.mat, results/<tag>.s1p|.s2p and results/<tag>.csv.
%   kind     : 'filter' | 'patch' | 'thru'
%   o        : options struct passed to the builder
%   sweepOpt : 'direct' or 'interp' (toolbox adaptive rational interpolation)
set(0,'DefaultFigureVisible','off');
if ~exist('results','dir'), mkdir('results'); end
switch kind
  case 'filter', [p, g] = build_filter(o);
  case 'patch',  [p, g] = build_patch(o);
  case 'thru',   [p, g] = build_thru(o);
end
t0 = tic;
md = mesh(p,'MaxEdgeLength',mel*1e-3); close all
nTri = size(md.Triangles,2); nTet = size(md.Tetrahedra,2);
tMesh = toc(t0);
f = fGHz(:).'*1e9;
t0 = tic;
fit = [];
if strcmpi(sweepOpt,'interp')
  [s, fit] = sparameters(p, f, 'SweepOption','interp');
else
  s = sparameters(p, f);
end
tSolve = toc(t0);
i = info(p); pf = sort(i.PortFrequency(:).');
S = s.Parameters;
solver = p.SolverType;
fprintf('%s: kind %s mel %.3f mm, tri %d tet %d, solver %s, sweep %s, %d outputs, %d solved freqs, mesh %.1f s, solve %.1f s\n', ...
        tag, kind, mel, nTri, nTet, solver, sweepOpt, numel(f), numel(pf), tMesh, tSolve);
save(fullfile('results',[tag '.mat']), 'f','S','pf','g','o','mel','nTri','nTet','tSolve','sweepOpt','solver','kind','fit');
np = size(S,1);
rfwrite(sparameters(S, f, 50), fullfile('results',sprintf('%s.s%dp', tag, np)));
fid = fopen(fullfile('results',[tag '.csv']),'w');
if np == 1
  fprintf(fid,'freq_GHz,S11_re,S11_im,S11_dB\n');
  for k = 1:numel(f)
    fprintf(fid,'%.6f,%.8f,%.8f,%.5f\n', f(k)/1e9, real(S(1,1,k)), imag(S(1,1,k)), 20*log10(abs(S(1,1,k))));
  end
else
  fprintf(fid,'freq_GHz,S11_re,S11_im,S21_re,S21_im,S12_re,S12_im,S22_re,S22_im,S11_dB,S21_dB\n');
  for k = 1:numel(f)
    fprintf(fid,'%.6f,%.8f,%.8f,%.8f,%.8f,%.8f,%.8f,%.8f,%.8f,%.5f,%.5f\n', f(k)/1e9, ...
      real(S(1,1,k)), imag(S(1,1,k)), real(S(2,1,k)), imag(S(2,1,k)), ...
      real(S(1,2,k)), imag(S(1,2,k)), real(S(2,2,k)), imag(S(2,2,k)), ...
      20*log10(abs(S(1,1,k))), 20*log10(abs(S(2,1,k))));
  end
end
fclose(fid);
end
