function final_plots(vdir)
% final_plots  Visual checks: MoM vs digitised thesis curves (verified CSVs).  Files in results/.
set(0,'DefaultFigureVisible','off');
d11 = readtable(fullfile(vdir,'fig3_33_S11_dB_verified.csv'),'TextType','string');
d21 = readtable(fullfile(vdir,'fig3_34_S21_dB_verified.csv'),'TextType','string');
A = readtable(fullfile('results','F_m100_gx3948.csv'));
B = readtable(fullfile('results','F_m070_gx3948.csv'));
P5 = readtable(fullfile('results','D_F_m050_gx3948.csv'));
P6 = readtable(fullfile('results','D_F_m060_gx3948.csv'));
for z = 1:2
  fig = figure('Position',[0 0 1600 1000]);
  for pnl = 1:2
    subplot(2,1,pnl); hold on; grid on
    if pnl == 1, d = d11; q = 'S11_dB'; else, d = d21; q = 'S21_dB'; end
    m = d.curve == "measured"; fd = d.curve == "fdtd";
    [ff, i] = sort(d.freq_GHz(fd)); vf = d.value(fd); vf = vf(i);
    plot(ff, vf, 'k-', 'LineWidth', 1.6, 'DisplayName', 'FD-TD, thesis (digitised)');
    plot(d.freq_GHz(m), d.value(m), '.', 'Color', [0.85 0.1 0.1], 'MarkerSize', 6, 'DisplayName', 'measured, thesis (digitised)');
    plot(A.freq_GHz, A.(q), '-', 'Color', [0.2 0.45 0.85], 'LineWidth', 1.0, 'DisplayName', 'MoM 1.0 mm');
    plot(B.freq_GHz, B.(q), '-', 'Color', [0.1 0.65 0.3], 'LineWidth', 1.0, 'DisplayName', 'MoM 0.7 mm');
    plot(P6.freq_GHz, P6.(q), 'o', 'Color', [0.6 0.3 0.8], 'MarkerSize', 5, 'DisplayName', 'MoM 0.6 mm (direct points)');
    plot(P5.freq_GHz, P5.(q), 's', 'Color', [0.95 0.55 0.1], 'MarkerSize', 6, 'LineWidth', 1.2, 'DisplayName', 'MoM 0.5 mm (direct points)');
    ylabel(['|' q(1:3) '| dB']); legend('Location','southwest');
    if z == 1, xlim([0 20]); ylim([-50 2]); else
      if pnl == 1, xlim([4 12]); ylim([-45 0]); else, xlim([4.5 9]); ylim([-50 0]); end
    end
  end
  xlabel('GHz');
  if z == 1, fn = 'plot_filter_full.png'; else, fn = 'plot_filter_zoom.png'; end
  exportgraphics(fig, fullfile('results', fn), 'Resolution', 100); close(fig);
end
% ---- patch
z = readtable(fullfile(vdir,'fig4_14_Zin_verified.csv'),'TextType','string');
files = {'patch_m400_Zedge.csv','patch_m400g335_Zedge.csv','patch_m400g6_Zedge.csv','patch_m250g6_Zedge.csv','patch_m200g6_Zedge.csv','patch_m160g6_Zedge.csv'};
labs  = {'MoM 4 mm, margin 16.75','MoM 4 mm, margin 33.5','MoM 4 mm, margin 6','MoM 2.5 mm, margin 6','MoM 2 mm, margin 6 (points)','MoM 1.6 mm, margin 6 (points)'};
cols = lines(numel(files));
fig = figure('Position',[0 0 1500 1100]);
qq = {'Zin_R_ohm','Zin_X_ohm'};
for pnl = 1:3
  subplot(3,1,pnl); hold on; grid on
  if pnl < 3
    fd = z.quantity == qq{pnl} & z.curve == "fdtd"; me = z.quantity == qq{pnl} & z.curve == "measured";
    [ff, i] = sort(z.freq_GHz(fd)); v = z.value(fd); v = v(i);
    plot(ff, v, 'k-', 'LineWidth', 1.6, 'DisplayName', 'FD-TD (Fig. 4.14)');
    plot(z.freq_GHz(me), z.value(me), 'rp', 'MarkerSize', 10, 'LineWidth', 1.5, 'DisplayName', 'measured (Fig. 4.14)');
  else
    de = z.quantity == "S11_dB_derived_from_digitised_RX_Z0_50";
    [ff, i] = sort(z.freq_GHz(de)); v = z.value(de); v = v(i);
    plot(ff, v, 'k-', 'LineWidth', 1.6, 'DisplayName', 'FD-TD |S11| from digitised R, X (50 ohm)');
    mR = z.quantity == "Zin_R_ohm" & z.curve == "measured"; mX = z.quantity == "Zin_X_ohm" & z.curve == "measured";
    zm = z.value(mR) + 1j*z.value(mX);
    plot(z.freq_GHz(mR), 20*log10(abs((zm-50)./(zm+50))), 'rp', 'MarkerSize', 10, 'LineWidth', 1.5, 'DisplayName', 'measured |S11| from Fig. 4.14 R, X (50 ohm)');
  end
  for k = 1:numel(files)
    T = readtable(fullfile('results', files{k}));
    if pnl == 1, y = T.R_A_ohm; elseif pnl == 2, y = T.X_A_ohm; else, y = T.S11_port_dB; end
    if height(T) < 50, sty = 'o'; else, sty = '-'; end
    plot(T.freq_GHz, y, sty, 'Color', cols(k,:), 'LineWidth', 1.0, 'DisplayName', labs{k});
  end
  xlim([2 2.5]); legend('Location','northeast');
  if pnl == 1, ylabel('R (ohm), patch edge'); elseif pnl == 2, ylabel('X (ohm), patch edge'); else, ylabel('|S11| dB (MoM: port, 50 ohm)'); xlabel('GHz'); end
end
exportgraphics(fig, fullfile('results','plot_patch.png'), 'Resolution', 100); close(fig);
end
