function run_jobs(outFile, caseIds, rs, levels, tag, tLimit, varargin)
%RUN_JOBS Resumable sweep of stripRac over the FU-13 rectangles.
%   run_jobs(outFile, caseIds, rs, levels, tag, tLimit, stripRacOptions...)
%   runs every (case, t/delta, level) not already in outFile with this tag,
%   in order of level (cheapest first), and stops starting new solves once
%   tLimit seconds have passed, so one MATLAB call stays short.  Call again
%   to resume.  Mesh level L uses hmaxFactor = 2^(1-L).
sigma = 5.8e7; mu0 = 4e-7*pi;
cases = struct('id', {'a','b','c','d'}, ...
    'w', {1e-3, 1e-3, 1e-3, 0.2e-3}, 't', {1e-3, 0.25e-3, 35e-6, 35e-6});
hdr = ['case,w_m,t_m,sigma,f_Hz,t_over_delta,mesh_level,nodes,Rac_over_Rdc,' ...
    'run,hmaxFactor,hs_m,hcorner_m,hgrad,RfarFactor,quarter,condElements,Rac_over_Rdc_powerform,' ...
    'Lint_Hpm,dL_Hpm,L0dc_Hpm,Ltot_Hpm,t_s\n'];
done = {};
if isfile(outFile)
    T = readtable(outFile, 'TextType', 'string', 'Delimiter', ',', 'VariableNamingRule', 'preserve');
    for k = 1:height(T)
        done{end+1} = sprintf('%s|%s|%g|%d', T.run(k), T.("case")(k), T.t_over_delta(k), T.mesh_level(k)); %#ok<AGROW>
    end
else
    fid = fopen(outFile, 'w'); fprintf(fid, hdr); fclose(fid);
end
t0 = tic;
for lev = levels
    for cid = caseIds
        c = cases([cases.id] == cid);
        for r = rs
            key = sprintf('%s|%s|%g|%d', tag, cid, r, lev);
            if any(strcmp(done, key)), continue; end
            if toc(t0) > tLimit
                fprintf('time limit reached; stopping before %s\n', key);
                return;
            end
            f = r^2/(pi*mu0*sigma*c.t^2);
            hf = 2^(1 - lev);
            [ra, Li, info] = stripRac(c.w, c.t, sigma, f, hf, varargin{:});
            tt = info.tMesh + info.tSolve + info.tDc;
            fprintf('%s %s t/d=%g level %d nodes %8d R/Rdc %.9f power %.9f Lint %.6e dL %.6e  %.1f s\n', ...
                tag, cid, r, lev, info.nodes, ra, info.ratioPower, Li, info.dL, tt);
            fid = fopen(outFile, 'a');
            fprintf(fid, '%s,%.6g,%.6g,%.6g,%.10g,%g,%d,%d,%.10f,%s,%.6g,%.6g,%.6g,%.6g,%g,%d,%d,%.10f,%.8e,%.8e,%.8e,%.8e,%.2f\n', ...
                cid, c.w, c.t, sigma, f, r, lev, info.nodes, ra, tag, hf, info.hs_m, info.hv_m, info.hgrad, ...
                info.RfarFactor, info.quarter, info.condElements, info.ratioPower, Li, info.dL, info.L0dc, info.Ltot, tt);
            fclose(fid);
        end
    end
end
fprintf('all jobs done for %s\n', tag);
end
