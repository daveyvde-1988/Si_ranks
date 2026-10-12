using UnityEngine;

public class UseAlienTakeOver : UsePoint
{
	[Tooltip("The unit type to kill if the previous unit is of this type")]
	public ObjectInfo KillPreviousType;

	[Tooltip("Effect data played on the previous unit if it is killed in the process")]
	public ImpactEffectData KillPreviousEffectData;

	[Tooltip("Sound effect data played on the previous unit if it is killed in the process")]
	public AudioEffectData KillPreviousSoundData;

	[Tooltip("Sound effect data played on the current unit when the transfer occurs")]
	public AudioEffectData MindTransferSoundData;

	public override bool CanUse(Unit caller)
	{
		if (!OwnerUnit)
		{
			return false;
		}
		if ((bool)OwnerUnit.ControlledBy)
		{
			return false;
		}
		if (!caller.ControlledBy)
		{
			return false;
		}
		if (caller.IsDirectControlled)
		{
			return false;
		}
		return true;
	}

	public override void OnUse(Unit caller)
	{
		if ((bool)MindTransferSoundData)
		{
			AudioEffectManager.RequestAudioEffect(MindTransferSoundData, OwnerUnit.transform.position);
		}
		if (Unit.CurrentUnit == caller)
		{
			Game.QueueCameraLerp();
		}
		if (!Game.GetIsClient())
		{
			base.OnUse(caller);
			Player controlledBy = caller.ControlledBy;
			if (Game.GetIsMultiplayer())
			{
				NetworkLayer.SendPlayerSelectUnit(controlledBy, OwnerUnit);
			}
			controlledBy.ControlledUnit = OwnerUnit;
			if (caller.IsFPSSpawned)
			{
				caller.DamageManager.SetHealth(0f, null, force: true);
			}
			if (GameEvents.OnTutorialTaskEvent != null && controlledBy == Player.CurrentPlayer)
			{
				GameEvents.OnTutorialTaskEvent(ETutorialTaskEvent.TRANSFER_MIND, gameObject);
			}
		}
		if ((bool)caller && caller.ObjectInfo == KillPreviousType && OwnerUnit.ObjectInfo != KillPreviousType)
		{
			if (!Game.GetIsClient())
			{
				caller.DamageManager.SetHealth(0f, null, force: true);
			}
			if ((bool)KillPreviousSoundData)
			{
				AudioEffectManager.RequestAudioEffect(KillPreviousSoundData, caller.transform.position);
			}
			if ((bool)KillPreviousEffectData)
			{
				ImpactEffectManager.RequestImpactEffect(KillPreviousEffectData, caller.transform.position, caller.transform.rotation);
			}
		}
	}
}
