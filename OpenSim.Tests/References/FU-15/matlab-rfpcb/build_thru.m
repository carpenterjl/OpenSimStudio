function [pcb, g] = build_thru(o)
% build_thru  Straight microstrip thru line with edge feeds at both board edges,
% used to extract the MoM line parameters (Z0, gamma) and the edge-port parasitic
% for de-embedding the patch feed line.  Same substrate, width and board x-extent
% as build_patch.  o.Lt = line length (mm) = board length along the line.
if nargin < 1, o = struct(); end
d = struct('Wf',4.48,'Lt',33.5,'BLx',73.7,'er',2.55,'h',1.59);
fn = fieldnames(d);
for k = 1:numel(fn), if ~isfield(o,fn{k}), o.(fn{k}) = d.(fn{k}); end, end
g = o; mm = 1e-3;
line = traceRectangular('Length',o.Wf*mm,'Width',o.Lt*mm,'Center',[0 0]);
gnd  = traceRectangular('Length',o.BLx*mm,'Width',o.Lt*mm,'Center',[0 0]);
sub = dielectric('Name','sub','EpsilonR',o.er,'LossTangent',0,'Thickness',o.h*mm);
pcb = pcbComponent;
pcb.Name = 'thru';
pcb.BoardShape = traceRectangular('Length',o.BLx*mm,'Width',o.Lt*mm,'Center',[0 0]);
pcb.BoardThickness = o.h*mm;
pcb.Layers = {line, sub, gnd};
pcb.FeedLocations = [0, -o.Lt/2*mm, 1, 3; 0, o.Lt/2*mm, 1, 3];
pcb.FeedDiameter = o.Wf/2*mm;
pcb.Conductor = metal('PEC');
end
