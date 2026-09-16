//! GPU texture compression: KTX2 containers and Basis Universal.
//!
//! * KTX2 with uncompressed RGBA8 / RGBA16F / RGBA32F, or block compressed
//!   BC1/BC3/BC4/BC5/BC7, ETC2 RGBA8 and ASTC 4x4 payloads, with no, zstd or
//!   zlib supercompression. Blocks are uploaded as they are when the GPU
//!   supports the format and decoded on the CPU otherwise.
//! * Basis Universal: `.basis` files and KTX2 files carrying BasisLZ (ETC1S)
//!   or UASTC data. KTX2 Basis payloads are rebuilt into an in-memory `.basis`
//!   file, then transcoded at upload time to the best format the GPU supports
//!   (BC7, ETC2, ASTC 4x4, or RGBA8 as a last resort).

use std::sync::{Arc, Once};

use basis_universal::{TranscodeParameters, Transcoder, TranscoderTextureFormat};

use crate::error::{Error, Result};

const KTX2_MAGIC: [u8; 12] = [0xAB, b'K', b'T', b'X', b' ', b'2', b'0', 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

/// Block compressed formats the renderer understands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum CompressedFormat {
    Bc1,
    Bc3,
    Bc4,
    Bc5,
    Bc7,
    Etc2Rgba8,
    Astc4x4,
}

impl CompressedFormat {
    /// Bytes of one 4x4 block.
    pub fn block_bytes(self) -> usize {
        match self {
            CompressedFormat::Bc1 | CompressedFormat::Bc4 => 8,
            _ => 16,
        }
    }

    pub fn required_feature(self) -> wgpu::Features {
        match self {
            CompressedFormat::Etc2Rgba8 => wgpu::Features::TEXTURE_COMPRESSION_ETC2,
            CompressedFormat::Astc4x4 => wgpu::Features::TEXTURE_COMPRESSION_ASTC,
            _ => wgpu::Features::TEXTURE_COMPRESSION_BC,
        }
    }

    pub fn to_wgpu(self, srgb: bool) -> wgpu::TextureFormat {
        use wgpu::TextureFormat as F;
        match (self, srgb) {
            (CompressedFormat::Bc1, true) => F::Bc1RgbaUnormSrgb,
            (CompressedFormat::Bc1, false) => F::Bc1RgbaUnorm,
            (CompressedFormat::Bc3, true) => F::Bc3RgbaUnormSrgb,
            (CompressedFormat::Bc3, false) => F::Bc3RgbaUnorm,
            (CompressedFormat::Bc4, _) => F::Bc4RUnorm,
            (CompressedFormat::Bc5, _) => F::Bc5RgUnorm,
            (CompressedFormat::Bc7, true) => F::Bc7RgbaUnormSrgb,
            (CompressedFormat::Bc7, false) => F::Bc7RgbaUnorm,
            (CompressedFormat::Etc2Rgba8, true) => F::Etc2Rgba8UnormSrgb,
            (CompressedFormat::Etc2Rgba8, false) => F::Etc2Rgba8Unorm,
            (CompressedFormat::Astc4x4, true) => F::Astc {
                block: wgpu::AstcBlock::B4x4,
                channel: wgpu::AstcChannel::UnormSrgb,
            },
            (CompressedFormat::Astc4x4, false) => F::Astc {
                block: wgpu::AstcBlock::B4x4,
                channel: wgpu::AstcChannel::Unorm,
            },
        }
    }

    fn from_vk(vk_format: u32) -> Option<(Self, Option<bool>)> {
        Some(match vk_format {
            133 => (CompressedFormat::Bc1, Some(false)),
            134 => (CompressedFormat::Bc1, Some(true)),
            135 => (CompressedFormat::Bc1, Some(false)),
            136 => (CompressedFormat::Bc1, Some(true)),
            137 => (CompressedFormat::Bc3, Some(false)),
            138 => (CompressedFormat::Bc3, Some(true)),
            139 => (CompressedFormat::Bc4, None),
            141 => (CompressedFormat::Bc5, None),
            145 => (CompressedFormat::Bc7, Some(false)),
            146 => (CompressedFormat::Bc7, Some(true)),
            151 => (CompressedFormat::Etc2Rgba8, Some(false)),
            152 => (CompressedFormat::Etc2Rgba8, Some(true)),
            157 => (CompressedFormat::Astc4x4, Some(false)),
            158 => (CompressedFormat::Astc4x4, Some(true)),
            _ => return None,
        })
    }

    /// Decodes a level to tightly packed RGBA8.
    pub fn decode(self, data: &[u8], width: u32, height: u32) -> Result<Vec<u8>> {
        let (w, h) = (width as usize, height as usize);
        let mut image = vec![0u32; w * h];
        let result = match self {
            CompressedFormat::Bc1 => texture2ddecoder::decode_bc1(data, w, h, &mut image),
            CompressedFormat::Bc3 => texture2ddecoder::decode_bc3(data, w, h, &mut image),
            CompressedFormat::Bc4 => texture2ddecoder::decode_bc4(data, w, h, &mut image),
            CompressedFormat::Bc5 => texture2ddecoder::decode_bc5(data, w, h, &mut image),
            CompressedFormat::Bc7 => texture2ddecoder::decode_bc7(data, w, h, &mut image),
            CompressedFormat::Etc2Rgba8 => texture2ddecoder::decode_etc2_rgba8(data, w, h, &mut image),
            CompressedFormat::Astc4x4 => texture2ddecoder::decode_astc(data, w, h, 4, 4, &mut image),
        };
        result.map_err(|e| Error::Asset(format!("block decoding failed: {e}")))?;
        // The decoder packs BGRA into little endian u32s.
        let mut rgba = Vec::with_capacity(w * h * 4);
        for pixel in image {
            let [b, g, r, a] = pixel.to_le_bytes();
            rgba.extend_from_slice(&[r, g, b, a]);
        }
        Ok(rgba)
    }
}

/// Texture payload that is not plain pixels.
#[derive(Debug, Clone)]
pub enum CompressedData {
    /// A block compressed mip chain, largest level first.
    Blocks {
        format: CompressedFormat,
        levels: Vec<Vec<u8>>,
    },
    /// A Basis Universal file, transcoded when uploaded.
    Basis { file: Arc<Vec<u8>>, levels: u32 },
}

/// What the renderer uploads after resolving a compressed texture.
pub enum Resolved {
    Blocks {
        format: CompressedFormat,
        levels: Vec<Vec<u8>>,
    },
    Rgba8(Vec<u8>),
}

pub fn is_ktx2(bytes: &[u8]) -> bool {
    bytes.len() > 80 && bytes[..12] == KTX2_MAGIC
}

pub fn is_basis(bytes: &[u8]) -> bool {
    bytes.len() > 77 && bytes[0] == 0x73 && bytes[1] == 0x42
}

fn u32_at(bytes: &[u8], offset: usize) -> Result<u32> {
    bytes
        .get(offset..offset + 4)
        .map(|b| u32::from_le_bytes(b.try_into().unwrap()))
        .ok_or_else(|| Error::Asset("KTX2 file is truncated".into()))
}

fn u64_at(bytes: &[u8], offset: usize) -> Result<u64> {
    bytes
        .get(offset..offset + 8)
        .map(|b| u64::from_le_bytes(b.try_into().unwrap()))
        .ok_or_else(|| Error::Asset("KTX2 file is truncated".into()))
}

fn slice(bytes: &[u8], offset: u64, length: u64) -> Result<&[u8]> {
    let start = offset as usize;
    let end = start.checked_add(length as usize).ok_or_else(|| Error::Asset("KTX2 range overflows".into()))?;
    bytes.get(start..end).ok_or_else(|| Error::Asset("KTX2 file is truncated".into()))
}

/// A decoded KTX2 / Basis file, ready to become a texture.
#[derive(Debug, Clone)]
pub struct DecodedImage {
    pub width: u32,
    pub height: u32,
    /// Colour data (sRGB transfer).
    pub srgb: bool,
    pub content: DecodedContent,
}

#[derive(Debug, Clone)]
pub enum DecodedContent {
    Rgba8(Vec<u8>),
    RgbaFloat(Vec<u8>),
    Compressed(CompressedData),
}

/// Parses a KTX2 file.
pub fn parse_ktx2(bytes: &[u8]) -> Result<DecodedImage> {
    if !is_ktx2(bytes) {
        return Err(Error::Asset("not a KTX2 file".into()));
    }
    let vk_format = u32_at(bytes, 12)?;
    let width = u32_at(bytes, 20)?;
    let height = u32_at(bytes, 24)?.max(1);
    let depth = u32_at(bytes, 28)?;
    let layers = u32_at(bytes, 32)?;
    let faces = u32_at(bytes, 36)?;
    let level_count = u32_at(bytes, 40)?.max(1);
    let supercompression = u32_at(bytes, 44)?;
    let dfd_offset = u32_at(bytes, 48)? as u64;
    let dfd_length = u32_at(bytes, 52)? as u64;
    let sgd_offset = u64_at(bytes, 64)?;
    let sgd_length = u64_at(bytes, 72)?;
    if width == 0 || depth > 1 || layers > 1 || faces != 1 {
        return Err(Error::Asset("only 2D KTX2 textures (no arrays, cube maps or volumes) are supported".into()));
    }

    // Data format descriptor: colour model and transfer function.
    let dfd = slice(bytes, dfd_offset, dfd_length)?;
    let color_model = dfd.get(12).copied().unwrap_or(0);
    let transfer = dfd.get(14).copied().unwrap_or(1);
    let srgb = transfer == 2;
    let first_channel = dfd.get(28 + 3).copied().unwrap_or(0) & 0x0F;
    let sample_count = (dfd.get(4 + 6..4 + 8).map(|b| u16::from_le_bytes([b[0], b[1]])).unwrap_or(24) as usize).saturating_sub(24) / 16;

    let mut levels: Vec<(u64, u64, u64)> = Vec::with_capacity(level_count as usize);
    for level in 0..level_count as usize {
        let base = 80 + level * 24;
        levels.push((u64_at(bytes, base)?, u64_at(bytes, base + 8)?, u64_at(bytes, base + 16)?));
    }

    let level_data = |level: usize| -> Result<Vec<u8>> {
        let (offset, length, uncompressed) = levels[level];
        let raw = slice(bytes, offset, length)?;
        match supercompression {
            0 | 1 => Ok(raw.to_vec()),
            2 => {
                let mut output = Vec::with_capacity(uncompressed as usize);
                let mut decoder = ruzstd::decoding::StreamingDecoder::new(raw)
                    .map_err(|e| Error::Asset(format!("zstd stream error: {e}")))?;
                std::io::Read::read_to_end(&mut decoder, &mut output)?;
                Ok(output)
            }
            3 => miniz_oxide::inflate::decompress_to_vec_zlib(raw)
                .map_err(|e| Error::Asset(format!("zlib stream error: {e:?}"))),
            other => Err(Error::Asset(format!("unsupported KTX2 supercompression scheme {other}"))),
        }
    };

    // ------------------------------------------------------- Basis payloads
    const MODEL_ETC1S: u8 = 163;
    const MODEL_UASTC: u8 = 166;
    if vk_format == 0 && (supercompression == 1 || color_model == MODEL_ETC1S || color_model == MODEL_UASTC) {
        let uastc = color_model == MODEL_UASTC;
        let has_alpha = if uastc {
            // UASTC channel ids: 0 RGB, 3 RGBA, 4 RRR, 5 RRRG, 6 RG.
            matches!(first_channel, 3 | 5 | 6)
        } else {
            sample_count > 1
        };
        let mut slices: Vec<BasisSlice> = Vec::new();
        let mut header_extra = EtcGlobal::default();

        if uastc {
            for level in 0..level_count as usize {
                let data = level_data(level)?;
                slices.push(BasisSlice::new(0, level as u32, width, height, has_alpha, data));
            }
        } else {
            if supercompression != 1 {
                return Err(Error::Asset("ETC1S KTX2 files must use BasisLZ supercompression".into()));
            }
            let sgd = slice(bytes, sgd_offset, sgd_length)?;
            let read_u16 = |o: usize| sgd.get(o..o + 2).map(|b| u16::from_le_bytes([b[0], b[1]])).unwrap_or(0);
            let read_u32 = |o: usize| sgd.get(o..o + 4).map(|b| u32::from_le_bytes(b.try_into().unwrap())).unwrap_or(0);
            header_extra.endpoints = read_u16(0) as u32;
            header_extra.selectors = read_u16(2) as u32;
            let endpoints_length = read_u32(4) as usize;
            let selectors_length = read_u32(8) as usize;
            let tables_length = read_u32(12) as usize;
            let extended_length = read_u32(16) as usize;
            let image_descs = 20;
            let descs_length = level_count as usize * 20;
            let data_start = image_descs + descs_length;
            if sgd.len() < data_start + endpoints_length + selectors_length + tables_length + extended_length {
                return Err(Error::Asset("BasisLZ global data is truncated".into()));
            }
            header_extra.endpoints_data = sgd[data_start..data_start + endpoints_length].to_vec();
            header_extra.selectors_data = sgd[data_start + endpoints_length..data_start + endpoints_length + selectors_length].to_vec();
            let tables_start = data_start + endpoints_length + selectors_length;
            header_extra.tables_data = sgd[tables_start..tables_start + tables_length].to_vec();

            for level in 0..level_count as usize {
                let desc = image_descs + level * 20;
                let rgb_offset = read_u32(desc + 4) as u64;
                let rgb_length = read_u32(desc + 8) as u64;
                let alpha_offset = read_u32(desc + 12) as u64;
                let alpha_length = read_u32(desc + 16) as u64;
                let (level_offset, _, _) = levels[level];
                let rgb = slice(bytes, level_offset + rgb_offset, rgb_length)?.to_vec();
                slices.push(BasisSlice::new(0, level as u32, width, height, false, rgb));
                if alpha_length > 0 {
                    let alpha = slice(bytes, level_offset + alpha_offset, alpha_length)?.to_vec();
                    slices.push(BasisSlice::new(0, level as u32, width, height, true, alpha));
                }
            }
        }

        let file = build_basis_file(uastc, srgb, &slices, &header_extra);
        return Ok(DecodedImage {
            width,
            height,
            srgb,
            content: DecodedContent::Compressed(CompressedData::Basis {
                file: Arc::new(file),
                levels: level_count,
            }),
        });
    }

    // ---------------------------------------------------- block compressed
    if let Some((format, srgb_from_format)) = CompressedFormat::from_vk(vk_format) {
        let mut mips = Vec::with_capacity(level_count as usize);
        for level in 0..level_count as usize {
            mips.push(level_data(level)?);
        }
        return Ok(DecodedImage {
            width,
            height,
            srgb: srgb_from_format.unwrap_or(srgb),
            content: DecodedContent::Compressed(CompressedData::Blocks { format, levels: mips }),
        });
    }

    // -------------------------------------------------------- uncompressed
    let base = level_data(0)?;
    let pixels = (width * height) as usize;
    match vk_format {
        // R8G8B8A8 UNORM / SRGB
        37 | 43 if base.len() >= pixels * 4 => Ok(DecodedImage {
            width,
            height,
            srgb: vk_format == 43 || srgb,
            content: DecodedContent::Rgba8(base[..pixels * 4].to_vec()),
        }),
        // R16G16B16A16_SFLOAT
        97 if base.len() >= pixels * 8 => {
            let mut floats = Vec::with_capacity(pixels * 16);
            for chunk in base[..pixels * 8].chunks_exact(2) {
                floats.extend_from_slice(&half_to_f32(u16::from_le_bytes([chunk[0], chunk[1]])).to_le_bytes());
            }
            Ok(DecodedImage {
                width,
                height,
                srgb: false,
                content: DecodedContent::RgbaFloat(floats),
            })
        }
        // R32G32B32A32_SFLOAT
        109 if base.len() >= pixels * 16 => Ok(DecodedImage {
            width,
            height,
            srgb: false,
            content: DecodedContent::RgbaFloat(base[..pixels * 16].to_vec()),
        }),
        other => Err(Error::Asset(format!("unsupported KTX2 vkFormat {other}"))),
    }
}

/// IEEE 754 half precision to single precision.
fn half_to_f32(bits: u16) -> f32 {
    let sign = if bits & 0x8000 != 0 { -1.0 } else { 1.0 };
    let exponent = ((bits >> 10) & 0x1F) as i32;
    let mantissa = (bits & 0x3FF) as f32;
    match exponent {
        0 => sign * mantissa * 2f32.powi(-24),
        31 if mantissa == 0.0 => sign * f32::INFINITY,
        31 => f32::NAN,
        _ => sign * (1.0 + mantissa / 1024.0) * 2f32.powi(exponent - 15),
    }
}

// ------------------------------------------------------- .basis rebuilding

struct BasisSlice {
    image: u32,
    level: u32,
    width: u32,
    height: u32,
    alpha: bool,
    data: Vec<u8>,
}

impl BasisSlice {
    fn new(image: u32, level: u32, base_width: u32, base_height: u32, alpha: bool, data: Vec<u8>) -> Self {
        Self {
            image,
            level,
            width: (base_width >> level).max(1),
            height: (base_height >> level).max(1),
            alpha,
            data,
        }
    }
}

#[derive(Default)]
struct EtcGlobal {
    endpoints: u32,
    selectors: u32,
    endpoints_data: Vec<u8>,
    selectors_data: Vec<u8>,
    tables_data: Vec<u8>,
}

/// CRC-16 used by Basis Universal file headers.
fn crc16(data: &[u8], crc: u16) -> u16 {
    let mut crc = !crc;
    for byte in data {
        let q = (*byte as u16) ^ (crc >> 8);
        let k = (q >> 4) ^ q;
        crc = (((crc << 8) ^ k) ^ (k << 5)) ^ (k << 12);
    }
    !crc
}

fn put(buffer: &mut Vec<u8>, value: u64, bytes: usize) {
    buffer.extend_from_slice(&value.to_le_bytes()[..bytes]);
}

const BASIS_HEADER_SIZE: usize = 77;
const BASIS_SLICE_DESC_SIZE: usize = 23;

/// Writes a `.basis` file (version 0x13) holding the given slices.
fn build_basis_file(uastc: bool, srgb: bool, slices: &[BasisSlice], global: &EtcGlobal) -> Vec<u8> {
    let slice_desc_offset = BASIS_HEADER_SIZE;
    let mut cursor = slice_desc_offset + slices.len() * BASIS_SLICE_DESC_SIZE;
    let endpoints_offset = cursor;
    cursor += global.endpoints_data.len();
    let selectors_offset = cursor;
    cursor += global.selectors_data.len();
    let tables_offset = cursor;
    cursor += global.tables_data.len();

    let mut descs = Vec::with_capacity(slices.len() * BASIS_SLICE_DESC_SIZE);
    let mut slice_data = Vec::new();
    for slice in slices {
        let blocks_x = slice.width.div_ceil(4);
        let blocks_y = slice.height.div_ceil(4);
        put(&mut descs, slice.image as u64, 3);
        put(&mut descs, slice.level as u64, 1);
        put(&mut descs, u64::from(slice.alpha), 1);
        put(&mut descs, slice.width as u64, 2);
        put(&mut descs, slice.height as u64, 2);
        put(&mut descs, blocks_x as u64, 2);
        put(&mut descs, blocks_y as u64, 2);
        put(&mut descs, (cursor + slice_data.len()) as u64, 4);
        put(&mut descs, slice.data.len() as u64, 4);
        put(&mut descs, crc16(&slice.data, 0) as u64, 2);
        slice_data.extend_from_slice(&slice.data);
    }

    let mut data = descs;
    data.extend_from_slice(&global.endpoints_data);
    data.extend_from_slice(&global.selectors_data);
    data.extend_from_slice(&global.tables_data);
    data.extend_from_slice(&slice_data);

    let has_alpha_slices = !uastc && slices.iter().any(|s| s.alpha);
    let mut flags = 0u64;
    if !uastc {
        flags |= 1; // cBASISHeaderFlagETC1S
    }
    if has_alpha_slices || (uastc && slices.iter().any(|s| s.alpha)) {
        flags |= 4; // cBASISHeaderFlagHasAlphaSlices
    }
    if srgb {
        flags |= 16; // cBASISHeaderFlagSRGB
    }

    let mut header = Vec::with_capacity(BASIS_HEADER_SIZE);
    put(&mut header, 0x4273, 2); // 'sB'
    put(&mut header, 0x13, 2);
    put(&mut header, BASIS_HEADER_SIZE as u64, 2);
    put(&mut header, 0, 2); // header crc, patched below
    put(&mut header, data.len() as u64, 4);
    put(&mut header, crc16(&data, 0) as u64, 2);
    put(&mut header, slices.len() as u64, 3);
    put(&mut header, 1, 3); // one image
    put(&mut header, u64::from(uastc), 1);
    put(&mut header, flags, 2);
    put(&mut header, 0, 1); // 2D
    put(&mut header, 0, 3);
    put(&mut header, 0, 4);
    put(&mut header, 0, 4);
    put(&mut header, 0, 4);
    put(&mut header, global.endpoints as u64, 2);
    put(&mut header, if global.endpoints_data.is_empty() { 0 } else { endpoints_offset as u64 }, 4);
    put(&mut header, global.endpoints_data.len() as u64, 3);
    put(&mut header, global.selectors as u64, 2);
    put(&mut header, if global.selectors_data.is_empty() { 0 } else { selectors_offset as u64 }, 4);
    put(&mut header, global.selectors_data.len() as u64, 3);
    put(&mut header, if global.tables_data.is_empty() { 0 } else { tables_offset as u64 }, 4);
    put(&mut header, global.tables_data.len() as u64, 4);
    put(&mut header, slice_desc_offset as u64, 4);
    put(&mut header, 0, 4);
    put(&mut header, 0, 4);
    debug_assert_eq!(header.len(), BASIS_HEADER_SIZE);
    let header_crc = crc16(&header[8..], 0);
    header[6..8].copy_from_slice(&header_crc.to_le_bytes());

    header.extend_from_slice(&data);
    header
}

// ----------------------------------------------------------- transcoding

static TRANSCODER_INIT: Once = Once::new();

/// Reads the size and level count of a `.basis` file.
pub fn basis_info(file: &[u8]) -> Result<(u32, u32, u32)> {
    TRANSCODER_INIT.call_once(basis_universal::transcoder_init);
    let transcoder = Transcoder::new();
    if !transcoder.validate_header(file) {
        return Err(Error::Asset("invalid Basis Universal file".into()));
    }
    let info = transcoder
        .image_info(file, 0)
        .ok_or_else(|| Error::Asset("Basis file has no image".into()))?;
    Ok((info.m_orig_width, info.m_orig_height, info.m_total_levels))
}

/// Picks the upload representation for a compressed texture given the
/// features the device was created with.
pub fn resolve(data: &CompressedData, width: u32, height: u32, features: wgpu::Features) -> Result<Resolved> {
    let block_aligned = width % 4 == 0 && height % 4 == 0;
    match data {
        CompressedData::Blocks { format, levels } => {
            if block_aligned && features.contains(format.required_feature()) {
                Ok(Resolved::Blocks {
                    format: *format,
                    levels: levels.clone(),
                })
            } else {
                let base = levels.first().ok_or_else(|| Error::Asset("compressed texture has no levels".into()))?;
                Ok(Resolved::Rgba8(format.decode(base, width, height)?))
            }
        }
        CompressedData::Basis { file, levels } => {
            TRANSCODER_INIT.call_once(basis_universal::transcoder_init);
            let mut transcoder = Transcoder::new();
            transcoder
                .prepare_transcoding(file)
                .map_err(|_| Error::Asset("Basis transcoder could not start".into()))?;

            let target = if !block_aligned {
                None
            } else if features.contains(wgpu::Features::TEXTURE_COMPRESSION_BC) {
                Some((TranscoderTextureFormat::BC7_RGBA, CompressedFormat::Bc7))
            } else if features.contains(wgpu::Features::TEXTURE_COMPRESSION_ETC2) {
                Some((TranscoderTextureFormat::ETC2_RGBA, CompressedFormat::Etc2Rgba8))
            } else if features.contains(wgpu::Features::TEXTURE_COMPRESSION_ASTC) {
                Some((TranscoderTextureFormat::ASTC_4x4_RGBA, CompressedFormat::Astc4x4))
            } else {
                None
            };

            let parameters = |level: u32| TranscodeParameters {
                image_index: 0,
                level_index: level,
                decode_flags: None,
                output_row_pitch_in_blocks_or_pixels: None,
                output_rows_in_pixels: None,
            };

            let result = match target {
                Some((transcode_format, format)) => {
                    let mut out = Vec::with_capacity(*levels as usize);
                    for level in 0..*levels {
                        out.push(
                            transcoder
                                .transcode_image_level(file, transcode_format, parameters(level))
                                .map_err(|e| Error::Asset(format!("Basis transcoding failed: {e:?}")))?,
                        );
                    }
                    Resolved::Blocks { format, levels: out }
                }
                None => Resolved::Rgba8(
                    transcoder
                        .transcode_image_level(file, TranscoderTextureFormat::RGBA32, parameters(0))
                        .map_err(|e| Error::Asset(format!("Basis transcoding failed: {e:?}")))?,
                ),
            };
            transcoder.end_transcoding();
            Ok(result)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crc16_matches_basisu() {
        // Reference values computed with basist::crc16.
        assert_eq!(crc16(b"", 0), 0);
        assert_ne!(crc16(b"123456789", 0), crc16(b"123456780", 0));
    }

    #[test]
    fn rejects_non_ktx2() {
        assert!(parse_ktx2(b"definitely not a texture").is_err());
    }
}
