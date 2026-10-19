param([string]$Name)
# Sequential MATLAB jobs; each one is a separate MATLAB command limited to 8.5 min by run_ml.ps1.
$d = Split-Path -Parent $MyInvocation.MyCommand.Path
$jobs = @{
  'F070' = @(
    @("run_sweep('filter', struct('gx',3.948), 0.7, 1:0.002:5.5, 'F_m070_gx3948_b1', 'interp')", 'F_m070_b1.log'),
    @("run_sweep('filter', struct('gx',3.948), 0.7, 5.5:0.002:10, 'F_m070_gx3948_b2', 'interp')", 'F_m070_b2.log'),
    @("run_sweep('filter', struct('gx',3.948), 0.7, 10:0.002:15, 'F_m070_gx3948_b3', 'interp')", 'F_m070_b3.log'),
    @("run_sweep('filter', struct('gx',3.948), 0.7, 15:0.002:20, 'F_m070_gx3948_b4', 'interp')", 'F_m070_b4.log'))
  'P34' = @(
    @("run_sweep('patch', struct(), 4, 2:0.0005:2.5, 'P_m400', 'interp')", 'P_m400.log'),
    @("run_sweep('thru', struct('Lt',16.75), 4, 2:0.005:2.5, 'T_L1675_m400', 'interp'); run_sweep('thru', struct('Lt',33.5), 4, 2:0.005:2.5, 'T_L3350_m400', 'interp')", 'T_m400.log'),
    @("run_sweep('patch', struct(), 3, 2:0.0005:2.5, 'P_m300', 'interp')", 'P_m300.log'),
    @("run_sweep('thru', struct('Lt',16.75), 3, 2:0.005:2.5, 'T_L1675_m300', 'interp'); run_sweep('thru', struct('Lt',33.5), 3, 2:0.005:2.5, 'T_L3350_m300', 'interp')", 'T_m300.log'))
  'V1' = @(
    @("verify_direct('filter', struct('gx',3.948), 1.0, [4.84 4.86 4.88 4.90 4.92 4.94 5.46 5.50 5.54 6.98 7.02 7.06 7.10 7.14 7.18 7.50 7.55 7.60 7.98 8.02 8.06 8.10 8.14 10.70 10.76 10.82 10.88 10.94 11.00 11.06 11.12 15.6 15.7 15.8], 'D_F_m100_gx3948')", 'D_F_m100.log'),
    @("run_sweep('thru', struct('Lt',16.75), 2.5, 2:0.005:2.5, 'T_L1675_m250', 'interp'); run_sweep('thru', struct('Lt',33.5), 2.5, 2:0.005:2.5, 'T_L3350_m250', 'interp')", 'T_m250.log'),
    @("run_sweep('filter', struct('gx',3.948,'Lfeed',8.464), 1.0, 1:0.01:20, 'F_m100_gx3948_L8464', 'interp')", 'F_m100_L8464.log'))
  'P6' = @(
    @("run_sweep('patch', struct('m',6), 4, 2:0.0005:2.5, 'P_m400_g6', 'interp'); run_sweep('patch', struct('m',6), 3, 2:0.0005:2.5, 'P_m300_g6', 'interp')", 'P_g6_a.log'),
    @("run_sweep('patch', struct('m',6), 2.5, 2:0.0005:2.5, 'P_m250_g6', 'interp')", 'P_g6_b.log'))
  'P2' = @(
    ,@("verify_direct('patch', struct('m',6), 2.0, [2.10 2.13 2.15 2.17 2.19 2.21 2.24 2.30], 'D_P_m200_g6')", 'D_P_m200_g6.log'))
  'P16' = @(
    @("verify_direct('patch', struct('m',6), 1.6, [2.12 2.17 2.20], 'D_P_m160_g6')", 'D_P_m160_a.log'),
    @("verify_direct('patch', struct('m',6), 1.6, [2.15 2.19 2.23], 'D_P_m160_g6')", 'D_P_m160_b.log'),
    @("verify_direct('patch', struct('m',6), 1.6, [2.21 2.26], 'D_P_m160_g6')", 'D_P_m160_c.log'))
  'F05' = @(
    @("verify_direct('filter', struct('gx',3.948), 0.5, [5.54 5.58 7.12 7.18], 'D_F_m050_gx3948')", 'D_F_m050_a.log'),
    @("verify_direct('filter', struct('gx',3.948), 0.5, [7.24 8.12 8.18 8.24], 'D_F_m050_gx3948')", 'D_F_m050_b.log'),
    @("verify_direct('filter', struct('gx',3.948), 0.5, [4.92 4.96 5.00 11.15], 'D_F_m050_gx3948')", 'D_F_m050_c.log'),
    @("verify_direct('filter', struct('gx',3.948), 0.5, [11.25 11.35 2.0 3.0], 'D_F_m050_gx3948')", 'D_F_m050_d.log'))
  'P13' = @(
    @("verify_direct('patch', struct('m',6,'Lfeed',6), 2.0, [2.13 2.17 2.19 2.21 2.24 2.30], 'D_P_m200_g6_L6')", 'D_P_m200_L6.log'),
    @("verify_direct('patch', struct('m',6,'Lfeed',6), 1.3, [2.19 2.23], 'D_P_m130_g6_L6')", 'D_P_m130_a.log'),
    @("verify_direct('patch', struct('m',6,'Lfeed',6), 1.3, [2.15 2.25], 'D_P_m130_g6_L6')", 'D_P_m130_b.log'),
    @("verify_direct('patch', struct('m',6,'Lfeed',6), 1.3, [2.21 2.29], 'D_P_m130_g6_L6')", 'D_P_m130_c.log'))
  'F05e' = @(
    ,@("verify_direct('filter', struct('gx',3.948), 0.5, [7.18 11.05], 'D_F_m050_gx3948')", 'D_F_m050_e.log'))
  'PM' = @(
    ,@("run_sweep('patch', struct('m',33.5), 4, 2:0.0005:2.5, 'P_m400_g335', 'interp')", 'P_m400_g335.log'))
  'F06' = @(
    @("verify_direct('filter', struct('gx',3.948), 0.7, [7.06 7.10 8.06 8.10 11.00 11.05 11.10], 'D_F_m070_gx3948')", 'D_F_m070.log'),
    @("verify_direct('filter', struct('gx',3.948), 0.6, [4.88 4.90 4.92 4.94 5.52 5.54 6.02 6.06], 'D_F_m060_gx3948')", 'D_F_m060_a.log'),
    @("verify_direct('filter', struct('gx',3.948), 0.6, [7.06 7.10 7.14 7.56 8.06 8.10 8.14], 'D_F_m060_gx3948')", 'D_F_m060_b.log'),
    @("verify_direct('filter', struct('gx',3.948), 0.6, [10.95 11.05 11.15 11.25 2.0 3.0 4.0], 'D_F_m060_gx3948')", 'D_F_m060_c.log'),
    @("run_sweep('filter', struct('gx',3.948,'Lfeed',8.464), 1.0, 1:0.01:6.5, 'F_m100_gx3948_L8464_b1', 'interp')", 'F_L8464_b1.log'),
    @("run_sweep('filter', struct('gx',3.948,'Lfeed',8.464), 1.0, 6.5:0.01:12, 'F_m100_gx3948_L8464_b2', 'interp')", 'F_L8464_b2.log'))
}
foreach ($j in $jobs[$Name]) {
  "=== $($j[1])"
  & "$d\run_ml.ps1" -Script $j[0] -Log $j[1] -TimeoutSec 510
  Get-Content (Join-Path $d $j[1])
  Get-Content (Join-Path $d ($j[1] + '.err')) -ErrorAction SilentlyContinue
}
