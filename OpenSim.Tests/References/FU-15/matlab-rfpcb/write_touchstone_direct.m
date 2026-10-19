% write_touchstone_direct.m - write Touchstone files (50 ohm) for the direct-solve CSVs D_*.csv
d = dir(fullfile('results','D_*.csv'));
for k = 1:numel(d)
  T = readtable(fullfile('results', d(k).name));
  [f, i] = sort(T.freq_GHz*1e9); T = T(i,:);
  [f, iu] = unique(f); T = T(iu,:);
  if any(strcmp(T.Properties.VariableNames,'S21_re'))
    S = zeros(2,2,numel(f));
    S(1,1,:) = T.S11_re + 1j*T.S11_im; S(2,1,:) = T.S21_re + 1j*T.S21_im;
    S(1,2,:) = T.S12_re + 1j*T.S12_im; S(2,2,:) = T.S22_re + 1j*T.S22_im;
    ext = '.s2p';
  else
    S = reshape(T.S11_re + 1j*T.S11_im, 1, 1, []);
    ext = '.s1p';
  end
  [~, base] = fileparts(d(k).name);
  rfwrite(sparameters(S, f, 50), fullfile('results', [base ext]));
  fprintf('%s -> %s%s (%d points)\n', d(k).name, base, ext, numel(f));
end
