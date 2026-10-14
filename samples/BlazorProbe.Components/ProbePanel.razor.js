const ECHO = "Echo";

export async function roundTrip(component) {
    try {
        return "js:" + await component.invokeMethodAsync(ECHO, "probe");
    } catch (error) {
        throw new Error(`JS/.NET round trip failed: ${error}`);
    }
}
