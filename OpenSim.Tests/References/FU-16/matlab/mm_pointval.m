function v = mm_pointval(msh, u, pts)
%MM_POINTVAL Value of a 6-node quadratic FE field u at points pts (n x 2).
T = msh.Elements;
ws = warning('off', 'MATLAB:triangulation:PtsNotInTriWarnId');   % midside nodes are not triangle corners
TR = triangulation(double(T(1:3,:))', msh.Nodes(1,:)', msh.Nodes(2,:)');
warning(ws);
[ti, bc] = pointLocation(TR, pts);
if any(isnan(ti)), error('mm_pointval: point outside the mesh'); end
L1 = bc(:,1); L2 = bc(:,2); L3 = bc(:,3);
N = [L1.*(2*L1-1), L2.*(2*L2-1), L3.*(2*L3-1), 4*L1.*L2, 4*L2.*L3, 4*L3.*L1];
v = sum(N .* u(T(:, ti))', 2);
end
