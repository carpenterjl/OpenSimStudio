function [pcb, g] = build_patch(o)
% build_patch  Sheen (1991) line-fed rectangular patch, Sec. 4.3, Fig. 4.11
% (thesis p.158-159 = PDF 158-159), as an RF PCB Toolbox pcbComponent (MoM).
%
% Geometry (geometry_verified.json; text p.158, Fig. 4.11 p.159):
%   patch 40.2 x 40.2 mm; feed line 4.48 mm wide ("nominally 50 ohm") connected
%   at the centre of one edge, no inset; er = 2.55, h = 1.59 mm; no loss tangent
%   given -> lossless; PEC zero-thickness metal (p.144).
% Port: edge feed at the end of the feed line, at the board edge.  The feed
%   length defaults to 16.75 mm = the FD-TD S11 reference plane (p.158).
% Ground plane size: NOT given in the thesis.  o.m = ground margin beyond the
%   patch on the three non-feed sides (default 16.75 mm, i.e. the same as the
%   feed side); varied in a sensitivity run.
if nargin < 1, o = struct(); end
d = struct('L',40.2,'W',40.2,'Wf',4.48,'Lfeed',16.75,'m',16.75,'er',2.55,'h',1.59);
fn = fieldnames(d);
for k = 1:numel(fn), if ~isfield(o,fn{k}), o.(fn{k}) = d.(fn{k}); end, end
g = o; mm = 1e-3;
% patch centred at the origin; feed along y entering the -y edge at x = 0
yP = -o.L/2;                          % feed-side (front) patch edge
yPort = yP - o.Lfeed;                 % board edge = port plane
ov = 0.05;
patch = traceRectangular('Length',o.W*mm,'Width',o.L*mm,'Center',[0 0]);
feed  = traceRectangular('Length',o.Wf*mm,'Width',(o.Lfeed+ov)*mm, ...
          'Center',[0, (yPort + (o.Lfeed+ov)/2)]*mm);
top = patch + feed;
xmin = -o.W/2 - o.m;  xmax = o.W/2 + o.m;
ymin = yPort;         ymax = o.L/2 + o.m;
BL = xmax - xmin;  BW = ymax - ymin;  c = [(xmin+xmax)/2, (ymin+ymax)/2];
g.BL = BL; g.BW = BW; g.center = c; g.yPort = yPort; g.yPatchEdge = yP;
gnd = traceRectangular('Length',BL*mm,'Width',BW*mm,'Center',c*mm);
sub = dielectric('Name','sub','EpsilonR',o.er,'LossTangent',0,'Thickness',o.h*mm);
pcb = pcbComponent;
pcb.Name = 'Sheen patch';
pcb.BoardShape = traceRectangular('Length',BL*mm,'Width',BW*mm,'Center',c*mm);
pcb.BoardThickness = o.h*mm;
pcb.Layers = {top, sub, gnd};
pcb.FeedLocations = [0, yPort*mm, 1, 3];
pcb.FeedDiameter = o.Wf/2*mm;
pcb.Conductor = metal('PEC');
end
