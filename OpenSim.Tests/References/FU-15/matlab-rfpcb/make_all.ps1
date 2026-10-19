# Post-processing pipeline (Python only; MATLAB runs are done by queue.ps1 / run_ml.ps1).
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$r = Join-Path $d 'results'
$refs = Split-Path -Parent $d
$v = Join-Path $refs 'sheen-thesis'   # the verified CSVs (the re-read's working folder was sheen-thesis-verify)
$t = Join-Path $refs 'sheen-thesis'
python -I "$d\merge_bands.py" "$r\F_m100_gx10.csv" "$r\F_m100_gx10_b1.csv" "$r\F_m100_gx10_b2.csv"
python -I "$d\merge_bands.py" "$r\F_m070_gx3948.csv" "$r\F_m070_gx3948_b1.csv" "$r\F_m070_gx3948_b2.csv" "$r\F_m070_gx3948_b3.csv" "$r\F_m070_gx3948_b4.csv"
python -I "$d\merge_bands.py" "$r\F_m100_gx3948_L8464.csv" "$r\F_m100_gx3948_L8464_b1.csv" "$r\F_m100_gx3948_L8464_b2.csv"
python -I "$d\analyze_filter.py" $v $r "$r\filter" 'm100=F_m100_gx3948.csv' 'm070=F_m070_gx3948.csv' 'm100_gx10=F_m100_gx10.csv' 'm100_L8464=F_m100_gx3948_L8464.csv' > "$r\filter_features_console.txt"
python -I "$d\direct_features.py" $r "$r\filter_mesh_points.csv" 'interp100=1.0:F_m100_gx3948.csv' 'direct100=1.0:D_F_m100_gx3948.csv' 'interp070=0.7:F_m070_gx3948.csv' 'direct070=0.7:D_F_m070_gx3948.csv' 'direct060=0.6:D_F_m060_gx3948.csv' 'direct050=0.5:D_F_m050_gx3948.csv' > "$r\filter_mesh_points_console.txt"
python -I "$d\analyze_patch.py" $v $t $r "$r\patch" 'm400=P_m400.csv,T_L1675_m400.csv,T_L3350_m400.csv' 'm300=P_m300.csv,T_L1675_m300.csv,T_L3350_m300.csv' 'm400g6=P_m400_g6.csv' 'm300g6=P_m300_g6.csv' 'm250g6=P_m250_g6.csv' 'm200g6=D_P_m200_g6.csv' 'm160g6=D_P_m160_g6.csv' 'm200g6L6=D_P_m200_g6_L6.csv@6.0' 'm400g335=P_m400_g335.csv' > "$r\patch_features_console.txt"
python -I "$d\convergence.py" $r "$r\patch_features.json" "$r\filter_mesh_points.csv" "$r\final" > "$r\convergence_console.txt"
python -I "$d\final_compare.py" $d > "$d\comparison_console.txt"
python -I "$d\manifest.py" $d "$d\run_manifest.csv"
