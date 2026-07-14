using System;
using System.Reflection;

namespace Soenneker.Serilog.Sinks.TUnit.Dtos;

internal sealed record ImmediateUpdatePublisher(
    Type ServiceProviderType,
    Type MessageBusType,
    MethodInvoker PublishInvoker);