//! Light probes: the light arriving at a point, from every direction, stored as
//! nine numbers per colour channel.
//!
//! A probe is captured by standing at a point and looking around - the same six
//! renders a cube capture does - and then projecting what was seen onto the
//! first three bands of spherical harmonics. Nine coefficients is enough to
//! reconstruct the *irradiance* of any environment to within a percent or so,
//! because a Lambertian surface integrates incoming light over a cosine lobe
//! and that lobe is so broad it erases everything sharper. The sharp detail is
//! what a reflection probe is for; this is the diffuse half.
//!
//! What it buys over a single ambient colour is direction. A room with a window
//! on one side lights the side of an object facing the window and leaves the
//! other side dark, which no scalar ambient term can do.

use crate::math::Vec3;

/// The number of coefficients in three bands: 1 + 3 + 5.
pub const COEFFICIENT_COUNT: usize = 9;

/// Irradiance at a point as spherical harmonic coefficients, one `Vec3` of
/// linear radiance per basis function.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LightProbe {
    pub coefficients: [Vec3; COEFFICIENT_COUNT],
}

impl Default for LightProbe {
    fn default() -> Self {
        Self::DARK
    }
}

/// The real spherical harmonic basis for the first three bands, evaluated in a
/// direction that must already be unit length.
fn basis(direction: Vec3) -> [f32; COEFFICIENT_COUNT] {
    let (x, y, z) = (direction.x, direction.y, direction.z);
    [
        0.282095,
        0.488603 * y,
        0.488603 * z,
        0.488603 * x,
        1.092548 * x * y,
        1.092548 * y * z,
        0.315392 * (3.0 * z * z - 1.0),
        1.092548 * x * z,
        0.546274 * (x * x - y * y),
    ]
}

impl LightProbe {
    /// A probe that contributes nothing.
    pub const DARK: Self = Self {
        coefficients: [Vec3::ZERO; COEFFICIENT_COUNT],
    };

    /// A probe holding the same radiance from every direction, which is what an
    /// ambient colour already was.
    pub fn uniform(radiance: Vec3) -> Self {
        let mut probe = Self::DARK;
        // Only the constant band is non-zero, and it integrates to 4 pi.
        probe.coefficients[0] = radiance * (0.282095 * 4.0 * std::f32::consts::PI);
        probe
    }

    /// Adds one sample of incoming linear radiance from `direction`, weighted by
    /// the solid angle it covers.
    pub fn accumulate(&mut self, direction: Vec3, radiance: Vec3, solid_angle: f32) {
        let basis = basis(direction);
        for (coefficient, weight) in self.coefficients.iter_mut().zip(basis) {
            *coefficient += radiance * (weight * solid_angle);
        }
    }

    /// The light a Lambertian surface facing `normal` reflects, per unit albedo.
    ///
    /// This is irradiance divided by pi, which is what the shading expects to
    /// multiply by an albedo. The band weights are the cosine lobe convolution:
    /// `pi`, `2 pi / 3` and `pi / 4`, each already divided by that pi.
    pub fn irradiance(&self, normal: Vec3) -> Vec3 {
        let basis = basis(normal);
        const BAND: [f32; COEFFICIENT_COUNT] = [
            1.0,
            2.0 / 3.0,
            2.0 / 3.0,
            2.0 / 3.0,
            0.25,
            0.25,
            0.25,
            0.25,
            0.25,
        ];
        let mut total = Vec3::ZERO;
        for i in 0..COEFFICIENT_COUNT {
            total += self.coefficients[i] * (basis[i] * BAND[i]);
        }
        // The projection is a fit, and a fit of a dark environment with a bright
        // patch in it can ring below zero. Negative light is not light.
        total.max(Vec3::ZERO)
    }

    /// Scales every coefficient, for exposure or for turning a probe down.
    pub fn scaled(&self, factor: f32) -> Self {
        let mut out = *self;
        for coefficient in &mut out.coefficients {
            *coefficient *= factor;
        }
        out
    }
}

/// One face of a capture: the pixels, their layout, and the camera basis that
/// produced them.
///
/// The directions come from the camera basis rather than from a cube map
/// convention, so the projection cannot drift out of step with however the
/// capture actually oriented itself.
pub struct FaceSamples<'a> {
    pub pixels: &'a [u8],
    pub size: u32,
    /// Where the camera was looking.
    pub forward: Vec3,
    /// Camera right, already orthogonal to `forward`.
    pub right: Vec3,
    /// Camera up, already orthogonal to both.
    pub up: Vec3,
    /// True when the pixels arrive as BGRA rather than RGBA.
    pub bgra: bool,
}

/// Undoes the sRGB transfer curve, because the readback is encoded for a screen
/// and light does not add up that way.
fn to_linear(channel: u8) -> f32 {
    let value = channel as f32 / 255.0;
    if value <= 0.04045 {
        value / 12.92
    } else {
        ((value + 0.055) / 1.055).powf(2.4)
    }
}

/// Projects one captured face into `probe`.
///
/// The face is assumed to come from a square camera with a ninety degree field
/// of view, which is what makes six of them cover the sphere exactly.
pub fn project_face(probe: &mut LightProbe, face: &FaceSamples<'_>) {
    let size = face.size as usize;
    if size == 0 || face.pixels.len() < size * size * 4 {
        return;
    }
    let step = 2.0 / face.size as f32;
    // Area of one texel on the plane one unit in front of the camera.
    let texel_area = step * step;

    for row in 0..size {
        // Rows arrive top first, and +1 is up.
        let v = 1.0 - ((row as f32 + 0.5) * step);
        for column in 0..size {
            let u = ((column as f32 + 0.5) * step) - 1.0;
            let at = ((row * size) + column) * 4;
            let (r, g, b) = if face.bgra {
                (face.pixels[at + 2], face.pixels[at + 1], face.pixels[at])
            } else {
                (face.pixels[at], face.pixels[at + 1], face.pixels[at + 2])
            };
            let radiance = Vec3::new(to_linear(r), to_linear(g), to_linear(b));

            let ray = (face.right * u) + (face.up * v) + face.forward;
            let length_squared = ray.length_squared();
            // The solid angle a texel covers shrinks towards the corners of the
            // face, where it is both further away and more steeply angled. Both
            // are one factor of the distance each, so the area falls off with
            // the cube of it.
            let solid_angle = texel_area / (length_squared * length_squared.sqrt());
            probe.accumulate(ray / length_squared.sqrt(), radiance, solid_angle);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The six camera bases a capture uses, as direction and up.
    const FACES: [(Vec3, Vec3); 6] = [
        (Vec3::X, Vec3::NEG_Y),
        (Vec3::NEG_X, Vec3::NEG_Y),
        (Vec3::Y, Vec3::Z),
        (Vec3::NEG_Y, Vec3::NEG_Z),
        (Vec3::Z, Vec3::NEG_Y),
        (Vec3::NEG_Z, Vec3::NEG_Y),
    ];

    /// Projects six faces of one flat colour, which is a uniform environment.
    fn project_uniform(level: u8, size: u32) -> LightProbe {
        let pixels = vec![level; (size * size * 4) as usize];
        let mut probe = LightProbe::DARK;
        for (forward, up) in FACES {
            let right = forward.cross(up).normalize();
            let true_up = right.cross(forward).normalize();
            project_face(
                &mut probe,
                &FaceSamples {
                    pixels: &pixels,
                    size,
                    forward,
                    right,
                    up: true_up,
                    bgra: false,
                },
            );
        }
        probe
    }

    #[test]
    fn six_faces_cover_the_whole_sphere() {
        // Every solid angle in the capture should add up to the 4 pi of a
        // sphere. If the texel weighting were wrong this is what would drift.
        //
        // Sampling texel centres is a midpoint rule, so a coarse face is a
        // little over: 16 square lands 0.1% high. Asserting that the error
        // shrinks with the face rather than picking one tolerance is what
        // distinguishes quadrature error from a wrong weight, which would not
        // converge at all.
        let total = |size| project_uniform(255, size).coefficients[0].x / 0.282095;
        let sphere = 4.0 * std::f32::consts::PI;
        let coarse = (total(8) - sphere).abs();
        let fine = (total(64) - sphere).abs();
        assert!(fine < 0.005, "a 64 square capture summed to {}, not 4 pi", total(64));
        assert!(fine < coarse, "refining the face did not improve it: {coarse} then {fine}");
    }

    #[test]
    fn a_uniform_environment_lights_every_direction_the_same() {
        let probe = project_uniform(255, 16);
        for normal in [Vec3::X, Vec3::NEG_X, Vec3::Y, Vec3::NEG_Y, Vec3::Z, Vec3::NEG_Z] {
            let irradiance = probe.irradiance(normal);
            assert!(
                (irradiance.x - 1.0).abs() < 0.01,
                "a white environment should reflect 1, got {irradiance:?} towards {normal:?}"
            );
        }
    }

    #[test]
    fn uniform_matches_a_projected_capture() {
        // The analytic shortcut and the integration have to agree, or one of
        // them is wrong about the normalisation.
        let projected = project_uniform(255, 16);
        let analytic = LightProbe::uniform(Vec3::ONE);
        assert!((projected.coefficients[0] - analytic.coefficients[0]).length() < 0.02);
    }

    #[test]
    fn one_bright_face_lights_the_side_facing_it() {
        // A single white face, the rest black: the point of a probe is that the
        // side facing the light is brighter than the side facing away.
        let bright = vec![255u8; 16 * 16 * 4];
        let dark = vec![0u8; 16 * 16 * 4];
        let mut probe = LightProbe::DARK;
        for (index, (forward, up)) in FACES.into_iter().enumerate() {
            let right = forward.cross(up).normalize();
            let true_up = right.cross(forward).normalize();
            project_face(
                &mut probe,
                &FaceSamples {
                    pixels: if index == 0 { &bright } else { &dark },
                    size: 16,
                    forward,
                    right,
                    up: true_up,
                    bgra: false,
                },
            );
        }

        let towards = probe.irradiance(Vec3::X);
        let away = probe.irradiance(Vec3::NEG_X);
        assert!(
            towards.x > away.x + 0.2,
            "facing the lit side should be brighter: {} against {}",
            towards.x,
            away.x
        );
    }

    #[test]
    fn srgb_is_decoded_before_the_light_is_added_up() {
        // Half way up the 8 bit range is about a fifth of the light, not half.
        let probe = project_uniform(128, 8);
        let reflected = probe.irradiance(Vec3::Y).x;
        assert!(
            (reflected - 0.2159).abs() < 0.01,
            "expected the sRGB decode of 128, got {reflected}"
        );
    }

    #[test]
    fn a_dark_probe_reflects_nothing() {
        assert_eq!(LightProbe::DARK.irradiance(Vec3::Y), Vec3::ZERO);
    }
}
