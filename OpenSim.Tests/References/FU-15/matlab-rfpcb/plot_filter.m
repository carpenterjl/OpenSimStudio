function plot_filter(vdir, tags, labels, outpng)
% plot_filter  Overlay MoM |S11|, |S21| on the digitised thesis curves (Figs. 3.33/3.34).
set(0,'DefaultFigureVisible','off');
d11 = readtable(fullfile(vdir,'fig3_33_S11_dB_verified.csv'),'TextType','string');
d21 = readtable(fullfile(vdir,'fig3_34_S21_dB_verified.csv'),'TextType','string');
fig = figure('Position',[0 0 1600 1000]);
cols = lines(numel(tags));
for pnl = 1:2
  subplot(2,1,pnl); hold on; grid on
  if pnl == 1, d = d11; q = 'S11_dB'; else, d = d21; q = 'S21_dB'; end
  m = d.curve == "measured"; fd = d.curve == "fdtd";
  [fm, im] = sort(d.freq_GHz(m)); vm = d.value(m); vm = vm(im);
  [ff, iff] = sort(d.freq_GHz(fd)); vf = d.value(fd); vf = vf(iff);
  plot(ff, vf, 'k-', 'LineWidth', 1.5, 'DisplayName', 'FD-TD (thesis, digitised)');
  plot(fm, vm, '.', 'Color', [0.85 0.1 0.1], 'MarkerSize', 5, 'DisplayName', 'measured (thesis, digitised)');
  for k = 1:numel(tags)
    T = readtable(fullfile('results',[tags{k} '.csv']));
    plot(T.freq_GHz, T.(q), '-', 'Color', cols(k,:), 'LineWidth', 1.0, 'DisplayName', labels{k});
  end
  xlim([0 20]); ylim([-50 2]); xlabel('GHz'); ylabel([strrep(q,'_',' ') ]);
  legend('Location','southwest');
end
exportgraphics(fig, outpng, 'Resolution', 110);
close(fig);
end
