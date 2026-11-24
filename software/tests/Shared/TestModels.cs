namespace Mouthtrap.Tests;

// ONNX graphs written with the ONNX protobuf schema, without trained weights.
internal static class TestModels
{
    // Identity from a float input "image" of shape 1x3x1x2 (a 2x1 RGB image as NCHW) to an output "jaw" of the same
    // shape, so the six outputs are the normalized channels in tensor order.
    public const string Identity = "CAg6YwoWCgVpbWFnZRIDamF3IghJZGVudGl0eRIJc3ludGhldGljWh8KBWltYWdlEhYKFAgBEhAKAggBCgIIAwoCCAEKAggCYh0KA2phdxIWChQIARIQCgIIAQoCCAMKAggBCgIIAkICEA0=";
}
