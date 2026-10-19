function plot_patch(vdir, zfiles, labels, outpng)
% plot_patch  Overlay MoM patch-edge R, X (de-embedding A and, if present, B) and port |S11|
% on the digitised thesis Fig. 4.14 (p.162) curves and markers.
set(0,'DefaultFigureVisible','off');
d = readtable(fullfile(vdir,'fig4_14_Zin_verified.csv'),'TextType','string');
fig = figure('Position',[0 0 1500 1100]);
cols = lines(numel(zfiles));
q = {'Zin_R_ohm','Zin_X_ohm'};
for pnl = 1:2
  subplot(3,1,pnl); hold on; grid on
  fd = d.quantity == q{pnl} & d.curve == "fdtd"; me = d.quantity == q{pnl} & d.curve == "measured";
  [ff, i] = sort(d.freq_GHz(fd)); v = d.value(fd); v = v(i);
  plot(ff, v, 'k-', 'LineWidth', 1.5, 'DisplayName', 'FD-TD (thesis Fig. 4.14)');
  plot(d.freq_GHz(me), d.value(me), 'rs', 'MarkerSize', 8, 'LineWidth', 1.5, 'DisplayName', 'measured (Fig. 4.14)');
  for k = 1:numel(zfiles)
    T = readtable(fullfile('results', zfiles{k}));
    if pnl == 1, y = T.R_A_ohm; else, y = T.X_A_ohm; end
    plot(T.freq_GHz, y, '-', 'Color', cols(k,:), 'DisplayName', [labels{k} ' (de-emb. A)']);
    if any(strcmp(T.Properties.VariableNames, 'R_B_ohm'))
      if pnl == 1, y = T.R_B_ohm; else, y = T.X_B_ohm; end
      plot(T.freq_GHz, y, '--', 'Color', cols(k,:), 'DisplayName', [labels{k} ' (de-emb. B)']);
    end
  end
  xlim([2 2.5]); ylabel(strrep(q{pnl},'_',' ')); legend('Location','northwest');
end
subplot(3,1,3); hold on; grid on
de = d.quantity == "S11_dB_derived_from_digitised_RX_Z0_50";
[ff, i] = sort(d.freq_GHz(de)); v = d.value(de); v = v(i);
plot(ff, v, 'k-', 'LineWidth', 1.5, 'DisplayName', 'FD-TD |S11| (from digitised R,X, 50 ohm)');
mR = d.quantity == "Zin_R_ohm" & d.curve == "measured"; mX = d.quantity == "Zin_X_ohm" & d.curve == "measured";
zm = d.value(mR) + 1j*d.value(mX);
plot(d.freq_GHz(mR), 20*log10(abs((zm-50)./(zm+50))), 'rs', 'MarkerSize', 8, 'LineWidth', 1.5, 'DisplayName', 'measured |S11| from Fig. 4.14 markers (50 ohm)');
for k = 1:numel(zfiles)
  T = readtable(fullfile('results', zfiles{k}));
  plot(T.freq_GHz, T.S11_port_dB, '-', 'Color', cols(k,:), 'DisplayName', [labels{k} ' port |S11|']);
end
xlim([2 2.5]); xlabel('GHz'); ylabel('|S11| dB'); legend('Location','southwest');
exportgraphics(fig, outpng, 'Resolution', 100);
close(fig);
end
