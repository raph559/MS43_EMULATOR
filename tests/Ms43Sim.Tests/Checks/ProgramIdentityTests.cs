using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Checks;

public class ProgramIdentityTests
{
    [Fact] public void Car_Release_And_E85_ShareOneIdentity_StockDiffers()
    {
        var car = ProgramIdentity.Of(FirmwareImage.FromBytes(TestBins.CarBytes));
        Assert.Equal(car, ProgramIdentity.Of(FirmwareImage.FromBytes(TestBins.ReleaseBytes)));
        Assert.Equal(car, ProgramIdentity.Of(FirmwareImage.FromBytes(TestBins.E85CamPortBytes)));
        Assert.NotEqual(car, ProgramIdentity.Of(FirmwareImage.FromBytes(TestBins.StockBytes)));
        Assert.True(ProgramIdentity.IsMs43X001(FirmwareImage.FromBytes(TestBins.CarBytes)));
        Assert.False(ProgramIdentity.IsMs43X001(FirmwareImage.FromBytes(TestBins.StockBytes)));
    }

    [Fact] public void TheProgrammingSignature_IsLeftOut_ButAProgramByteIsNot()
    {
        var car = TestBins.CarBytes;
        var signed = car.ToArray();
        signed[0x4FFE6] ^= 0xFF;                                               // the 26-byte signature a flashing tool writes
        Assert.Equal(ProgramIdentity.Of(FirmwareImage.FromBytes(car)), ProgramIdentity.Of(FirmwareImage.FromBytes(signed)));
        var changed = car.ToArray();
        changed[0x4FFE5] ^= 0xFF;
        Assert.NotEqual(ProgramIdentity.Of(FirmwareImage.FromBytes(car)), ProgramIdentity.Of(FirmwareImage.FromBytes(changed)));
    }
}
