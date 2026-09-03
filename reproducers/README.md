# Reproducers

`trace_methane_stripper.dwxmz` - saved by DWSIM 10.2.3 (automation), fails the
same way in the 10.2.4 GUI. A 12-stage reboiled stripper (DistillationColumn,
Full_Reflux with reflux ratio 0, bottoms 18.547 lbmol/h, feed on stage 1, top
stage 34 psig, 1 psi drop) on Peng-Robinson over 69 DWSIM database compounds,
with the flash settings DWSIM.Automation gives a new flowsheet
(UsePhaseIdentificationAlgorithm = False, NestedLoops_v2 = False). The feed is
synthetic: 20 lbmol/h of a Fischer-Tropsch-type naphtha (Anderson-Schulz-Flory
paraffins at alpha 0.85 cut to C4-C12, olefins and alcohols alongside) at
120 degF and 36 psig carrying 2.0e-4 mole fraction of methane and a few other
dissolved gases at similar trace levels.

The stage estimates are a converged solution of this very column from an
independent stage-by-stage adiabatic-flash solver on the same property
package (48 kW on the reboiler stage: 166.0 degF on stage 1 to 252.6 degF on
stage 12, overhead 1.45 lbmol/h, bottoms 18.55 lbmol/h, every dissolved gas
and all but a trace of the propane in the overhead), with
AutoUpdateInitialEstimates off. Open and solve:

    Failed to fulfill mass balance for Methane: Relative Error = 0.999999999999339 [Tolerance = 0.0001]

Every other compound balances; the methane is in neither product, and the
reported products (282 and 321 degF) have drifted far from the estimates.
Take the methane out and the message names ethane; take every dissolved gas
out and it names propane.

The two changes on this branch are aimed at it: the bubble-point solver no
longer mirrors negative tridiagonal liquid rates into the stage liquid, and
the post-solve balance check measures a trace against one part per million
of the total feed instead of against itself. See the issue this branch
accompanies.
