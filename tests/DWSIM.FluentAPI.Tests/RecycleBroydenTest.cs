using System;
using System.Linq;
using DWSIM.Automation.FluentAPI;
using DWSIM.Automation.FluentAPI.Builders;
using DWSIM.Interfaces.Enums;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.UnitOperations.SpecialOps;
using FS = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.FluentAPI.Tests
{
    // Recycle "Global Convergence (Broyden)" used to wreck its tear stream on the second solver
    // pass. broydn returns a step (new x = x + P), but the solver mixed 0.3 of the point with 0.7 of
    // the step, so temperature, pressure and flow collapsed to about 0.3 of their values (the flow
    // going negative). The mass-flow residual handed to Broyden was also an unsigned sum, so it could
    // only ever push the flow one way. A water/ethanol heater loop with Broyden on must converge to
    // the same steady state as plain successive substitution. https://github.com/DanWBR/dwsim10/issues/63
    internal static class RecycleBroydenTest
    {
        public static void Run()
        {
            // Feed 100 kg/h of a 50/50 water/ethanol mixture at 25 C and 200 kPa into a mixer with the
            // recycle return, heat to 60 C, split in half, half back through the recycle. At steady
            // state the split closes R = (100 + R) / 2, so the return settles at 100 kg/h, 60 C, 200 kPa.
            var fs = FS.Create("RecycleBroyden")
                       .WithCompound("Water")
                       .WithCompound("Ethanol")
                       .WithPropertyPackage(PropertyPackages.PengRobinson);

            var feed = fs.AddMaterialStream("FEED")
                         .At((25.0 + 273.15).Kelvin(), 200000.0.Pascal())
                         .WithMassFlow((100.0 / 3600.0).KgPerSecond())
                         .WithComposition(c => c.Mass("Water", 0.5).Mass("Ethanol", 0.5));
            var mixed = fs.AddMaterialStream("MIXED");
            var hot = fs.AddMaterialStream("HOT");
            var prod = fs.AddMaterialStream("PRODUCT");
            var tear = fs.AddMaterialStream("TEAR");

            // the recycle return, seeded with an estimate the solver iterates away from
            var ret = fs.AddMaterialStream("RETURN")
                        .At((60.0 + 273.15).Kelvin(), 200000.0.Pascal())
                        .WithMassFlow((50.0 / 3600.0).KgPerSecond())
                        .WithComposition(c => c.Mass("Water", 0.5).Mass("Ethanol", 0.5));

            fs.AddMixer("MIX-1").ConnectFeed(feed, 0).ConnectFeed(ret, 1).ConnectProduct(mixed, 0);
            fs.AddHeater("HTR-1").ConnectFeed(mixed, 0).ConnectProduct(hot, 0)
              .WithOutletTemperature((60.0 + 273.15).Kelvin());
            fs.AddSplitter("SPL-1").ConnectFeed(hot, 0).ConnectProduct(prod, 0).ConnectProduct(tear, 1)
              .Configure(s => { s.Ratios.Clear(); s.Ratios.Add(0.5); s.Ratios.Add(0.5); s.Ratios.Add(0.0); });
            var rec = fs.AddUnitOperation(ObjectType.OT_Recycle, "REC-1").ConnectFeed(tear, 0).ConnectProduct(ret, 0);

            var recycle = (Recycle)rec.Object;
            recycle.AccelerationMethod = AccelMethod.GlobalBroyden;
            recycle.MaximumIterations = 100;
            recycle.ConvergenceParameters.Temperatura = 0.01;
            recycle.ConvergenceParameters.Pressao = 1.0;
            recycle.ConvergenceParameters.VazaoMassica = 1.0E-05;

            var errors = fs.TrySolve();
            if (errors.Count > 0)
                throw new Exception("the Broyden recycle did not solve: " +
                                    string.Join("; ", errors.Select(e => e.Message)));

            var r = ret.Object;
            double wKgH = r.Phases[0].Properties.massflow.GetValueOrDefault() * 3600.0;
            double tK = r.Phases[0].Properties.temperature.GetValueOrDefault();
            double pPa = r.Phases[0].Properties.pressure.GetValueOrDefault();

            Console.WriteLine($"  Broyden recycle return: {wKgH:F2} kg/h, {tK:F2} K, {pPa:F0} Pa");

            // the bug left the return at ~0.3 of its values (99.94 K, 60,000 Pa, -2.50 kg/h)
            if (wKgH <= 0.0)
                throw new Exception($"return mass flow went non-positive: {wKgH:F2} kg/h");

            Check("return mass flow (kg/h)", wKgH, 100.0, 3.0);
            Check("return temperature (K)", tK, 333.15, 1.0);
            Check("return pressure (Pa)", pPa, 200000.0, 2000.0);
        }

        // A Broyden recycle used to freeze every other recycle on the flowsheet. After each unconverged
        // pass the solver wrote the Broyden update into the tear streams through SetOutletStreamProperties,
        // but it called that on every recycle, and a substitution recycle's Values hold its outlet as it
        // was before the recycle copied its inlet across. The copy was undone on every pass: the
        // substitution tear stream kept the temperature, pressure, mass flow and enthalpy it started with,
        // and that recycle could never converge. Two independent water/ethanol heater loops on one
        // flowsheet, one on Broyden and one on substitution, must both close.
        public static void RunBesideASubstitutionRecycle() => RunTwoLoops("RecycleBroydenBesideSubstitution", 0.5);

        // Once the Broyden recycle has converged, a slower recycle keeps the solver looping, and the Broyden
        // update goes on being called with variables that no longer move. The rank-one update then divided
        // zero by zero, filled its matrix with NaN and handed the converged tear stream a NaN temperature
        // and pressure. Loop B sends 0.8 of its stream back and needs about 35 passes; loop A must hold.
        public static void RunBesideASlowerSubstitutionRecycle() => RunTwoLoops("RecycleBroydenBesideSlowerSubstitution", 0.8);

        private static void RunTwoLoops(string name, double backB)
        {
            var fs = FS.Create(name)
                       .WithCompound("Water")
                       .WithCompound("Ethanol")
                       .WithPropertyPackage(PropertyPackages.PengRobinson);

            // loop A is the loop of Run(); loop B heats to 80 C and its return starts far from the answer
            var retA = AddHeaterLoop(fs, "A", 60.0, 0.5, 50.0, 60.0, AccelMethod.GlobalBroyden);
            var retB = AddHeaterLoop(fs, "B", 80.0, backB, 20.0, 25.0, AccelMethod.None);

            var errors = fs.TrySolve();
            if (errors.Count > 0)
                throw new Exception("the two recycle loops did not solve: " +
                                    string.Join("; ", errors.Select(e => e.Message)));

            foreach (var (loop, ret, back, tOutK) in new[] { ("A", retA, 0.5, 333.15), ("B", retB, backB, 353.15) })
            {
                var r = ret.Object;
                double wKgH = r.Phases[0].Properties.massflow.GetValueOrDefault() * 3600.0;
                double tK = r.Phases[0].Properties.temperature.GetValueOrDefault();

                Console.WriteLine($"  loop {loop} recycle return: {wKgH:F2} kg/h, {tK:F2} K");

                // the first bug left loop B's return where it started, 20 kg/h at 298.15 K
                Check($"loop {loop} return mass flow (kg/h)", wKgH, 100.0 * back / (1.0 - back), 3.0);
                Check($"loop {loop} return temperature (K)", tK, tOutK, 1.0);
            }
        }

        // 100 kg/h of 50/50 water/ethanol at 25 C and 200 kPa, mixed with the recycle return, heated,
        // split, the fraction back through a recycle: the return settles at 100 back / (1 - back) kg/h
        // at the heater outlet temperature.
        private static MaterialStreamBuilder AddHeaterLoop(FS fs, string loop, double heaterOutC, double back,
                                                           double returnKgH, double returnC, AccelMethod method)
        {
            var feed = fs.AddMaterialStream("FEED-" + loop)
                         .At((25.0 + 273.15).Kelvin(), 200000.0.Pascal())
                         .WithMassFlow((100.0 / 3600.0).KgPerSecond())
                         .WithComposition(c => c.Mass("Water", 0.5).Mass("Ethanol", 0.5));
            var mixed = fs.AddMaterialStream("MIXED-" + loop);
            var hot = fs.AddMaterialStream("HOT-" + loop);
            var prod = fs.AddMaterialStream("PRODUCT-" + loop);
            var tear = fs.AddMaterialStream("TEAR-" + loop);
            var ret = fs.AddMaterialStream("RETURN-" + loop)
                        .At((returnC + 273.15).Kelvin(), 200000.0.Pascal())
                        .WithMassFlow((returnKgH / 3600.0).KgPerSecond())
                        .WithComposition(c => c.Mass("Water", 0.5).Mass("Ethanol", 0.5));

            fs.AddMixer("MIX-" + loop).ConnectFeed(feed, 0).ConnectFeed(ret, 1).ConnectProduct(mixed, 0);
            fs.AddHeater("HTR-" + loop).ConnectFeed(mixed, 0).ConnectProduct(hot, 0)
              .WithOutletTemperature((heaterOutC + 273.15).Kelvin());
            fs.AddSplitter("SPL-" + loop).ConnectFeed(hot, 0).ConnectProduct(prod, 0).ConnectProduct(tear, 1)
              .Configure(s => { s.Ratios.Clear(); s.Ratios.Add(1.0 - back); s.Ratios.Add(back); s.Ratios.Add(0.0); });
            var rec = fs.AddUnitOperation(ObjectType.OT_Recycle, "REC-" + loop).ConnectFeed(tear, 0).ConnectProduct(ret, 0);

            var recycle = (Recycle)rec.Object;
            recycle.AccelerationMethod = method;
            recycle.MaximumIterations = 100;
            recycle.ConvergenceParameters.Temperatura = 0.01;
            recycle.ConvergenceParameters.Pressao = 1.0;
            recycle.ConvergenceParameters.VazaoMassica = 1.0E-05;

            return ret;
        }

        private static void Check(string what, double value, double expected, double tol)
        {
            if (Math.Abs(value - expected) > tol)
                throw new Exception($"{what}: expected {expected} +/- {tol}, got {value}");
        }
    }
}
