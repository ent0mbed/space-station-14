package main

import (
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"spacestation14.io/replay-converter/internal/canonical"
)

func main() {
	inputPath := flag.String("input", "", "canonical stream produced by Content.Replay.Export")
	outputPath := flag.String("output", "", "output replay directory")
	flag.Parse()
	if *inputPath == "" || *outputPath == "" {
		fmt.Fprintln(os.Stderr, "usage: replay-converter -input canonical.rplstream -output output-directory")
		os.Exit(2)
	}
	if err := run(*inputPath, *outputPath); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run(inputPath, outputPath string) error {
	input, err := os.Open(inputPath)
	if err != nil {
		return err
	}
	defer input.Close()

	reader, err := canonical.NewReader(input)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(outputPath, 0o755); err != nil {
		return err
	}

	for {
		record, err := reader.Next()
		if err != nil {
			if err == io.EOF {
				return fmt.Errorf("canonical stream ended without an end record")
			}
			return err
		}
		switch record.Kind {
		case canonical.KindHeader:
			if err = os.WriteFile(filepath.Join(outputPath, "source-replay.yml"), record.Payload, 0o644); err != nil {
				return err
			}
		case canonical.KindEnd:
			return os.WriteFile(filepath.Join(outputPath, "conversion.complete"), record.Payload, 0o644)
		default:
			return fmt.Errorf("record kind %d is not supported by the initial packer", record.Kind)
		}
	}
}
