//! CPU side texture data and image decoding.

use crate::error::{Error, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum TextureFormat {
    /// 8 bit RGBA, values interpreted as sRGB (colour data).
    Rgba8UnormSrgb = 0,
    /// 8 bit RGBA, linear (normal maps, metallic-roughness, masks).
    Rgba8Unorm = 1,
    /// 32 bit float RGBA, used for HDR environment maps.
    Rgba32Float = 2,
}

impl TextureFormat {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => TextureFormat::Rgba8Unorm,
            2 => TextureFormat::Rgba32Float,
            _ => TextureFormat::Rgba8UnormSrgb,
        }
    }

    #[inline]
    pub fn bytes_per_pixel(self) -> u32 {
        match self {
            TextureFormat::Rgba8UnormSrgb | TextureFormat::Rgba8Unorm => 4,
            TextureFormat::Rgba32Float => 16,
        }
    }

    pub(crate) fn to_wgpu(self) -> wgpu::TextureFormat {
        match self {
            TextureFormat::Rgba8UnormSrgb => wgpu::TextureFormat::Rgba8UnormSrgb,
            TextureFormat::Rgba8Unorm => wgpu::TextureFormat::Rgba8Unorm,
            TextureFormat::Rgba32Float => wgpu::TextureFormat::Rgba32Float,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum WrapMode {
    Repeat = 0,
    ClampToEdge = 1,
    MirrorRepeat = 2,
}

impl WrapMode {
    pub fn from_u32(value: u32) -> Self {
        match value {
            1 => WrapMode::ClampToEdge,
            2 => WrapMode::MirrorRepeat,
            _ => WrapMode::Repeat,
        }
    }

    pub(crate) fn to_wgpu(self) -> wgpu::AddressMode {
        match self {
            WrapMode::Repeat => wgpu::AddressMode::Repeat,
            WrapMode::ClampToEdge => wgpu::AddressMode::ClampToEdge,
            WrapMode::MirrorRepeat => wgpu::AddressMode::MirrorRepeat,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct SamplerDesc {
    pub wrap_u: WrapMode,
    pub wrap_v: WrapMode,
    pub linear_filter: bool,
    pub mipmaps: bool,
    /// Clamped to the device limit when the sampler is created.
    pub anisotropy: u16,
}

impl Default for SamplerDesc {
    fn default() -> Self {
        Self {
            wrap_u: WrapMode::Repeat,
            wrap_v: WrapMode::Repeat,
            linear_filter: true,
            mipmaps: true,
            anisotropy: 8,
        }
    }
}

/// Decoded image data waiting to be uploaded to the GPU.
#[derive(Debug, Clone)]
pub struct Texture {
    pub name: String,
    pub width: u32,
    pub height: u32,
    pub format: TextureFormat,
    /// Tightly packed pixels, `width * height * format.bytes_per_pixel()`.
    pub pixels: Vec<u8>,
    pub sampler: SamplerDesc,
    /// Block compressed or Basis Universal payload; `pixels` is empty then.
    pub compressed: Option<crate::compressed::CompressedData>,
    pub(crate) version: u32,
}

impl Texture {
    pub fn new(width: u32, height: u32, format: TextureFormat, pixels: Vec<u8>) -> Result<Self> {
        let expected = (width as usize)
            .saturating_mul(height as usize)
            .saturating_mul(format.bytes_per_pixel() as usize);
        if width == 0 || height == 0 || pixels.len() != expected {
            return Err(Error::InvalidArgument(format!(
                "texture {width}x{height} expects {expected} bytes, got {}",
                pixels.len()
            )));
        }
        Ok(Self {
            name: String::new(),
            width,
            height,
            format,
            pixels,
            sampler: SamplerDesc::default(),
            compressed: None,
            version: 1,
        })
    }

    /// Solid colour texture, used for the default material slots.
    pub fn solid(color: [u8; 4], srgb: bool) -> Self {
        Self {
            name: String::from("solid"),
            width: 1,
            height: 1,
            format: if srgb {
                TextureFormat::Rgba8UnormSrgb
            } else {
                TextureFormat::Rgba8Unorm
            },
            pixels: color.to_vec(),
            sampler: SamplerDesc {
                mipmaps: false,
                ..Default::default()
            },
            compressed: None,
            version: 1,
        }
    }

    /// Decodes PNG / JPEG / BMP / TGA / HDR from an in-memory buffer.
    /// HDR images are kept as `Rgba32Float`, everything else becomes RGBA8.
    pub fn from_encoded_bytes(bytes: &[u8], srgb: bool) -> Result<Self> {
        if crate::compressed::is_ktx2(bytes) {
            return Self::from_decoded(crate::compressed::parse_ktx2(bytes)?, srgb);
        }
        if crate::compressed::is_basis(bytes) {
            let (width, height, levels) = crate::compressed::basis_info(bytes)?;
            return Self::from_decoded(
                crate::compressed::DecodedImage {
                    width,
                    height,
                    srgb,
                    content: crate::compressed::DecodedContent::Compressed(crate::compressed::CompressedData::Basis {
                        file: std::sync::Arc::new(bytes.to_vec()),
                        levels,
                    }),
                },
                srgb,
            );
        }
        let reader = image::ImageReader::new(std::io::Cursor::new(bytes))
            .with_guessed_format()
            .map_err(|e| Error::Asset(format!("cannot detect image format: {e}")))?;
        let format = reader.format();
        let image = reader
            .decode()
            .map_err(|e| Error::Asset(format!("cannot decode image: {e}")))?;
        let hdr = matches!(format, Some(image::ImageFormat::Hdr));
        let (width, height) = (image.width(), image.height());
        if hdr {
            let rgba = image.to_rgba32f();
            Ok(Self {
                name: String::new(),
                width,
                height,
                format: TextureFormat::Rgba32Float,
                pixels: bytemuck::cast_slice(rgba.as_raw()).to_vec(),
                sampler: SamplerDesc::default(),
                compressed: None,
                version: 1,
            })
        } else {
            let rgba = image.to_rgba8();
            Ok(Self {
                name: String::new(),
                width,
                height,
                format: if srgb {
                    TextureFormat::Rgba8UnormSrgb
                } else {
                    TextureFormat::Rgba8Unorm
                },
                pixels: rgba.into_raw(),
                sampler: SamplerDesc::default(),
                compressed: None,
                version: 1,
            })
        }
    }

    /// Wraps a KTX2 / Basis decode result. The caller decides the colour space
    /// from how the texture is used (colour vs. data), as for PNG or JPEG.
    fn from_decoded(image: crate::compressed::DecodedImage, srgb: bool) -> Result<Self> {
        use crate::compressed::DecodedContent;
        let format = if srgb { TextureFormat::Rgba8UnormSrgb } else { TextureFormat::Rgba8Unorm };
        let (format, pixels, compressed) = match image.content {
            DecodedContent::Rgba8(pixels) => (format, pixels, None),
            DecodedContent::RgbaFloat(pixels) => (TextureFormat::Rgba32Float, pixels, None),
            DecodedContent::Compressed(data) => (format, Vec::new(), Some(data)),
        };
        Ok(Self {
            name: String::new(),
            width: image.width,
            height: image.height,
            format,
            pixels,
            sampler: SamplerDesc::default(),
            compressed,
            version: 1,
        })
    }

    pub fn from_file(path: &str, srgb: bool) -> Result<Self> {
        let bytes = std::fs::read(path)?;
        let mut texture = Self::from_encoded_bytes(&bytes, srgb)?;
        texture.name = path.to_string();
        Ok(texture)
    }

    #[inline]
    pub fn touch(&mut self) {
        self.version = self.version.wrapping_add(1).max(1);
    }

    #[inline]
    pub fn version(&self) -> u32 {
        self.version
    }

    /// Number of mip levels for a full chain, or 1 when mipmaps are disabled.
    pub fn mip_level_count(&self) -> u32 {
        if !self.sampler.mipmaps {
            return 1;
        }
        32 - self.width.max(self.height).leading_zeros()
    }
}
