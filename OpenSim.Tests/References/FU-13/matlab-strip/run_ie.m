function run_ie(outFile, caseIds, rs, levels, tLimit)
%RUN_IE Resumable sweep of the integral-equation cross-check ieRac.
sigma = 5.8e7; mu0 = 4e-7*pi;
cases = struct('id', {'a','b','c','d'}, ...
    'w', {1e-3, 1e-3, 1e-3, 0.2e-3}, 't', {1e-3, 0.25e-3, 35e-6, 35e-6});
done = {};
if isfile(outFile)
    T = readtable(outFile, 'TextType', 'string', 'Delimiter', ',', 'VariableNamingRule', 'preserve');
    for k = 1:height(T)
        done{end+1} = sprintf('%s|%g|%d', T.("case")(k), T.t_over_delta(k), T.ie_level(k)); %#ok<AGROW>
    end
else
    fid = fopen(outFile, 'w');
    fprintf(fid, 'case,w_m,t_m,sigma,f_Hz,t_over_delta,ie_level,nx,ny,cells_quarter,Rac_over_Rdc_IE,dL_IE_Hpm,t_s\n');
    fclose(fid);
end
t0 = tic;
for lev = levels
    for cid = caseIds
        c = cases([cases.id] == cid);
        for r = rs
            key = sprintf('%s|%g|%d', cid, r, lev);
            if any(strcmp(done, key)), continue; end
            if toc(t0) > tLimit
                fprintf('time limit reached; stopping before %s\n', key);
                return;
            end
            f = r^2/(pi*mu0*sigma*c.t^2);
            tt = tic;
            [ra, info] = ieRac(c.w, c.t, sigma, f, lev);
            tt = toc(tt);
            fprintf('IE %s t/d=%g level %d N %6d R/Rdc %.9f dL %.6e %.1f s\n', cid, r, lev, info.N, ra, info.dL, tt);
            fid = fopen(outFile, 'a');
            fprintf(fid, '%s,%.6g,%.6g,%.6g,%.10g,%g,%d,%d,%d,%d,%.10f,%.8e,%.2f\n', ...
                cid, c.w, c.t, sigma, f, r, lev, info.nx, info.ny, info.N, ra, info.dL, tt);
            fclose(fid);
        end
    end
end
fprintf('all IE jobs done\n');
end
