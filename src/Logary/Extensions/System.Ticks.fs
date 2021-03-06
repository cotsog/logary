namespace Logary

open System.Runtime.CompilerServices
open System.Runtime.InteropServices

[<Extension>]
module Ticks =
  /// Convert the ticks value to a Gauge.
  [<CompiledName("ToGauge"); Extension>]
  let toGauge (ticks: int64) =
    Gauge ((float ticks * float Constants.NanosPerTick), Scaled (Seconds, float Constants.NanosPerSecond))