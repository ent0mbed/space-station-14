package segment

import (
	"bytes"
	"context"
	"encoding/binary"
	"fmt"
	"hash/crc32"

	"github.com/klauspost/compress/zstd"
)

type Job struct {
	ID        uint32
	FirstTick uint64
	LastTick  uint64
	Keyframe  []byte
	Deltas    []byte
}

type Result struct {
	ID               uint32
	FirstTick        uint64
	LastTick         uint64
	Keyframe         []byte
	Deltas           []byte
	KeyframeChecksum uint32
	DeltaChecksum    uint32
}

// EncodeAll runs bounded workers. Results may arrive out of order; IDs provide deterministic commit order.
func EncodeAll(ctx context.Context, workers int, jobs <-chan Job) <-chan Result {
	if workers < 1 {
		workers = 1
	}
	out := make(chan Result, workers)
	done := make(chan struct{}, workers)

	for range workers {
		go func() {
			defer func() { done <- struct{}{} }()
			encoder, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1))
			if err != nil {
				return
			}
			defer encoder.Close()

			for {
				select {
				case <-ctx.Done():
					return
				case job, ok := <-jobs:
					if !ok {
						return
					}
					result := Result{
						ID: job.ID, FirstTick: job.FirstTick, LastTick: job.LastTick,
						Keyframe:         encoder.EncodeAll(job.Keyframe, nil),
						Deltas:           encoder.EncodeAll(job.Deltas, nil),
						KeyframeChecksum: crc32.ChecksumIEEE(job.Keyframe),
						DeltaChecksum:    crc32.ChecksumIEEE(job.Deltas),
					}
					select {
					case out <- result:
					case <-ctx.Done():
						return
					}
				}
			}
		}()
	}

	go func() {
		for range workers {
			<-done
		}
		close(out)
	}()
	return out
}

func EncodeMetadata(result Result) []byte {
	var output bytes.Buffer
	_ = binary.Write(&output, binary.LittleEndian, result.ID)
	_ = binary.Write(&output, binary.LittleEndian, result.FirstTick)
	_ = binary.Write(&output, binary.LittleEndian, result.LastTick)
	_ = binary.Write(&output, binary.LittleEndian, uint64(len(result.Keyframe)))
	_ = binary.Write(&output, binary.LittleEndian, uint64(len(result.Deltas)))
	_ = binary.Write(&output, binary.LittleEndian, result.KeyframeChecksum)
	_ = binary.Write(&output, binary.LittleEndian, result.DeltaChecksum)
	return output.Bytes()
}

func ObjectName(id uint32, kind string) string {
	return fmt.Sprintf("segments/%08d.%s.zst", id, kind)
}
