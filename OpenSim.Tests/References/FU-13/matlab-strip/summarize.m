% summarize.m - collect the FEM runs, the integral-equation cross-check and
% the far-boundary / symmetry / mesh-scatter checks into the deliverable CSVs.
opts = {'TextType', 'string', 'Delimiter', ',', 'VariableNamingRule', 'preserve'};
M = readAll('final_main_*.csv', opts);
M = M(M.run == "main", :);
Rf = readAll('final_rfar_*.csv', opts);
IE = readtable('ie_results.csv', opts{:});
FU = readAll('final_full*.csv', opts);
SC = readAll('strip_rac_scatter.csv', opts);

% ---- 1. the requested CSV (main FEM sequence) ----
M = sortrows(M, {'case', 't_over_delta', 'mesh_level'});
out = M(:, {'case','w_m','t_m','sigma','f_Hz','t_over_delta','mesh_level','nodes','Rac_over_Rdc'});
fid = fopen('strip_rac_results.csv', 'w');
fprintf(fid, 'case,w_m,t_m,sigma,f_Hz,t_over_delta,mesh_level,nodes,Rac_over_Rdc\n');
for k = 1:height(out)
    fprintf(fid, '%s,%.6g,%.6g,%.6g,%.10g,%g,%d,%d,%.10f\n', out.("case")(k), out.w_m(k), out.t_m(k), ...
        out.sigma(k), out.f_Hz(k), out.t_over_delta(k), out.mesh_level(k), out.nodes(k), out.Rac_over_Rdc(k));
end
fclose(fid);

% ---- 2. summary per case and t/delta ----
fid = fopen('strip_rac_summary.csv', 'w');
fprintf(fid, ['case,w_m,t_m,t_over_delta,f_Hz,levels,finest_level,nodes_finest,Rac_over_Rdc_finest,' ...
    'change_two_finest,rel_change_two_finest,monotone,observed_order,Rac_over_Rdc_Richardson,' ...
    'Rfar40_level,Rac_over_Rdc_Rfar40,Rfar40_minus_main_same_level,' ...
    'IE_levels,IE_finest,IE_observed_order,IE_Richardson,FE_finest_minus_IE_Richardson,' ...
    'mesh_scatter_spread,mesh_scatter_level,mesh_scatter_n,uncertainty_abs,Rdc_ohm_per_m,Rac_ohm_per_m,Lint_Hpm,dL_FE_Hpm,dL_IE_finest_Hpm\n']);
cases = unique(M.("case"));
fprintf('%-2s %5s %12s | %-60s | %12s %9s %6s %12s | %12s %12s | %9s\n', 'c', 't/d', 'f_Hz', ...
    'FE levels 1..N', 'd(two fin)', 'p', 'mono', 'Richardson', 'IE Richard.', 'FE-IE', 'U');
for ci = 1:numel(cases)
    for r = unique(M.t_over_delta(M.("case") == cases(ci))).'
        S = M(M.("case") == cases(ci) & M.t_over_delta == r, :);
        S = sortrows(S, 'mesh_level');
        R = S.Rac_over_Rdc; n = numel(R);
        [d2, p, mono, Rx] = richardson(R);
        % far boundary doubled
        F = Rf(Rf.("case") == cases(ci) & Rf.t_over_delta == r, :);
        if ~isempty(F), F = sortrows(F, 'mesh_level'); end
        if ~isempty(F)
            lf = F.mesh_level(end); rf40 = F.Rac_over_Rdc(end);
            dRf = rf40 - S.Rac_over_Rdc(S.mesh_level == lf);
        else
            lf = NaN; rf40 = NaN; dRf = NaN;
        end
        % integral equation
        I = sortrows(IE(IE.("case") == cases(ci) & IE.t_over_delta == r, :), 'ie_level');
        [~, pI, ~, RxI] = richardson(I.Rac_over_Rdc_IE);
        dFI = R(end) - RxI;
        % mesh-to-mesh scatter (perturbed mesh parameters), where measured
        sc = NaN; scl = NaN; scn = 0;
        if ~isempty(SC)
            Z = SC(SC.("case") == cases(ci) & SC.t_over_delta == r, :);
            if height(Z) > 1, sc = max(Z.Rac_over_Rdc) - min(Z.Rac_over_Rdc); scl = max(Z.mesh_level); scn = height(Z); end
        end
        U = max([abs(d2), abs(dFI), abs(dRf), sc], [], 'omitnan');
        Rdc = 1/(S.sigma(1)*S.w_m(1)*S.t_m(1));
        lv = sprintf('%.9f ', R);
        fprintf('%-2s %5g %12.6g | %-60s | %+12.3e %9.3g %6d %12.9f | %12.9f %+12.3e | %9.2e\n', ...
            cases(ci), r, S.f_Hz(1), strtrim(lv), d2, p, mono, Rx, RxI, dFI, U);
        fprintf(fid, ['%s,%.6g,%.6g,%g,%.10g,%d,%d,%d,%.10f,%.4e,%.4e,%d,%.4g,%.10f,' ...
            '%g,%.10f,%.4e,%d,%.10f,%.4g,%.10f,%.4e,%.4e,%g,%d,%.4e,%.8g,%.8g,%.8e,%.8e,%.8e\n'], ...
            cases(ci), S.w_m(1), S.t_m(1), r, S.f_Hz(1), n, S.mesh_level(end), S.nodes(end), R(end), ...
            d2, d2/R(end), mono, p, Rx, lf, rf40, dRf, height(I), I.Rac_over_Rdc_IE(end), pI, RxI, dFI, ...
            sc, scl, scn, U, Rdc, Rdc*R(end), S.Lint_Hpm(end), S.dL_Hpm(find(~isnan(S.dL_Hpm), 1, 'last')), I.dL_IE_Hpm(end));
    end
end
fclose(fid);

% ---- 3. checks CSV (far boundary x2, full section) ----
C = [Rf; FU];
fid = fopen('strip_rac_checks.csv', 'w');
fprintf(fid, 'case,t_over_delta,mesh_level,run,RfarFactor,quarter,nodes,Rac_over_Rdc,Rac_over_Rdc_main_same_level,difference\n');
for k = 1:height(C)
    mm = M(M.("case") == C.("case")(k) & M.t_over_delta == C.t_over_delta(k) & M.mesh_level == C.mesh_level(k), :);
    fprintf(fid, '%s,%g,%d,%s,%g,%d,%d,%.10f,%.10f,%.4e\n', C.("case")(k), C.t_over_delta(k), C.mesh_level(k), ...
        C.run(k), C.RfarFactor(k), C.quarter(k), C.nodes(k), C.Rac_over_Rdc(k), mm.Rac_over_Rdc(1), ...
        C.Rac_over_Rdc(k) - mm.Rac_over_Rdc(1));
end
fclose(fid);
disp('wrote strip_rac_results.csv, strip_rac_summary.csv, strip_rac_checks.csv');

function [d2, p, mono, Rx] = richardson(R)
% last three values of a level sequence with h halving each level
n = numel(R);
d2 = R(n) - R(n-1);
p = NaN; Rx = NaN; mono = 0;
if n >= 3
    d1 = R(n-1) - R(n-2);
    if d1*d2 > 0 && abs(d2) < abs(d1)
        mono = 1;
        p = log2(d1/d2);
        Rx = R(n) + d2/(2^p - 1);
    end
end
end

function T = readAll(pattern, opts)
T = table();
L = dir(pattern);
for i = 1:numel(L)
    T = [T; readtable(L(i).name, opts{:})]; %#ok<AGROW>
end
end