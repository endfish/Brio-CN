using Brio.Capabilities.Debug;
using Brio.Entities.Core;
using Brio.Resources;
using Dalamud.Interface;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Brio.Entities.Debug;

public class DebugEntity(IServiceProvider provider) : Entity(FixedId, provider)
{
    public const string FixedId = "debug_entity";

    public override string FriendlyName => Localize.Get("ui.entities.debug", "Debug");
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Bug;

    public override EntityFlags Flags => EntityFlags.AllowOutsideGpose;

    public override bool IsAttached => true;

    public override void OnAttached()
    {
        AddCapability(ActivatorUtilities.CreateInstance<DebugCapability>(_serviceProvider, this));
    }
}
