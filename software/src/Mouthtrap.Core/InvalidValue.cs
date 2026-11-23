namespace Mouthtrap.Core;

// A value that cannot be used, such as a crop outside the frame or a non-finite model output. Name is the parameter
// or field that holds it.
public sealed record InvalidValue(string Name, string Message);
