namespace Logary.Internals

open Hopac
open Logary
open Logary.Internals

/// The protocol for a targets runtime path (not the shutdown).
type TargetMessage =
  /// Log and send something that can be acked with the message.
  | Log of message:Message * ack:IVar<unit>
  /// Flush messages! Also, reply when you're done flushing your queue.
  | Flush of ack:IVar<unit> * nack:Promise<unit>

/// Logary's way to talk with Targets as seen from the Targets.
///
/// Targets are responsible for selecting over these channels in order to handle
/// shutdown and messages.
type TargetAPI =
  /// Gives you a way to perform internal logging and communicate with Logary.
  abstract runtimeInfo: RuntimeInfo
  /// A ring buffer that gives a Message to log and an ACK-IVar to signal after
  /// logging the message.
  abstract requests: RingBuffer<TargetMessage>
  /// A channel that the target needs to select on and then ACK once the target
  /// has fully shut down. 
  abstract shutdownCh: Ch<IVar<unit>>