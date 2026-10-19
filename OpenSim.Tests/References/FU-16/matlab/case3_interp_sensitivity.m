function case3_interp_sensitivity(NIs, lev)
%CASE3_INTERP_SENSITIVITY The ER section on one mesh with OpenSim's
% Fritsch-Carlson H(B) interpolant ('opensim' variant of bh_curve) instead of
% pchip: the part of any MATLAB-OpenSim difference that is the interpolant, not
% the solver. Appends the 'opensim' rows to case3_er_nonlinear.csv.
case3_er_nonlinear(NIs, lev, 'opensim');
end
