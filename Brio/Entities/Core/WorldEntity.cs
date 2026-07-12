using Dalamud.Interface;
using System;
using Brio.Resources;

namespace Brio.Entities.Core;

public class WorldEntity : Entity
{
    public override string FriendlyName => Localize.Get("ui.entities.world", "World");
    public override FontAwesomeIcon Icon => FontAwesomeIcon.EarthOceania;
    public override bool IsAttached => true;

    public override EntityFlags Flags => EntityFlags.DisableDraw;

    public WorldEntity(IServiceProvider provider) : base("world", provider)
    {
        OnAttached();
    }

    public override void OnAttached()
    {
        //AddCapability(ActivatorUtilities.CreateInstance<WorldCapability>(_serviceProvider, this));
    }
}
