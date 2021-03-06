﻿#region License & Metadata

// The MIT License (MIT)
// 
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
// 
// 
// Created On:   2019/03/02 18:29
// Modified On:  2019/04/18 13:19
// Modified By:  Alexis

#endregion




using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Newtonsoft.Json.Converters;
using SuperMemoAssistant.Extensions;
using SuperMemoAssistant.Services;

namespace SuperMemoAssistant.Plugins.LaTeX
{
  public class LaTeXDocument
  {
    #region Properties & Fields - Non-Public

    private LaTeXCfg Config    { get; }
    private string   Selection { get; set; }
    private string   Html      { get; set; }

    #endregion




    #region Constructors

    public LaTeXDocument(
      LaTeXCfg config,
      string   html,
      string             selection = null)
    {
      Config    = config;
      Html      = html;
      Selection = selection ?? html;
    }

    #endregion




    #region Methods

    public string ConvertImagesToLaTeX()
    {
      var newSelection  = Selection.Clone() as string;
      var allImagesData = GetAllImagesLaTeXCode();

      foreach (var (html, latex) in allImagesData)
      {
        // Escape <, > and & before replacing in HTML
        var latexCode = latex.FromBase64()
                             .Replace("&", " &amp; ")
                             .Replace("<", " &lt; ")
                             .Replace(">", " &gt; ");
                             
        newSelection = newSelection.ReplaceFirst(html,
                                                 latexCode);
      }
      Html = Html.Replace(Selection,
                          newSelection);
      Selection = newSelection;

      return Html;
    }

    public string ConvertLaTeXToImages()
    {
      string newSelection = LaTeXConst.RE.LaTeXError.Replace(Selection,
                                                             string.Empty);

      var filters = Config.Filters;

      var allTaggedMatches = filters.Select(
        f => (f.Value, f.Key.Matches(Selection))
      );

      foreach (var taggedMatches in allTaggedMatches)
      {
        var itemsOccurences = new Dictionary<string, int>();
        var processedMatches = GenerateImages(taggedMatches.Item1,
                                              taggedMatches.Item2);

        foreach (var processedMatch in processedMatches)
        {
          var (success, imgHtmlOrError, fullHtml) = processedMatch;

          int nb = itemsOccurences.SafeGet(fullHtml, 0) + 1;
          itemsOccurences[fullHtml] = nb;

          if (success)
            try
            {
              newSelection = newSelection.ReplaceFirst(fullHtml,
                                                     imgHtmlOrError); // <--- if you already replaced previous copies,
                                                                      //      the earliest remaining one is in the first position
            }
            catch (Exception ex)
            {
              success        = false;
              imgHtmlOrError = ex.Message;
            }

          if (success == false)
            newSelection = newSelection.ReplaceNth(fullHtml,
                                                   GenerateErrorHtml(fullHtml,
                                                                     imgHtmlOrError),
                                                   nb);
        }
      }

      Html = Html.Replace(Selection,
                          newSelection);
      Selection = newSelection;

      return Html;
    }


    private (double w, double h, double v) GetMetrics(string filePath)
    {
      var dimsFile = filePath.Replace("png", "dims");
      if (!File.Exists(dimsFile))
        throw new ArgumentException($"File \"{dimsFile}\" does not exist.");

      var metrics = File.ReadAllText(dimsFile);

      // Extract metrics using Regex
      var v = new Regex(@"(?<=depth:\s*)\d*\.?\d*(?=pt)").Match(metrics).Value;
      var h = new Regex(@"(?<=height:\s*)\d*\.?\d*(?=pt)").Match(metrics).Value;

      //
      var numberFormat = (System.Globalization.NumberFormatInfo) CultureInfo.InvariantCulture.NumberFormat.Clone();
      numberFormat.NumberDecimalSeparator = ".";

      double cmrFactor = 2.1 / 2.32;
      double mtpro2Factor = 2.0 / 2.32;

      double conv = 0.2554 / 1.1 * mtpro2Factor;
      double depth = double.Parse(v, numberFormat);
      double height = double.Parse(h, numberFormat);

      return (0, (height + depth) * conv, (depth+0.1) * conv);
    }
    private string GenerateImgHtml(string filePath,
                                   string latexCode)
    {
      if (File.Exists(filePath) == false)
        throw new ArgumentException($"File \"{filePath}\" does not exist.");

      var code = latexCode.ToBase64();

      var res = Svc.SM.Registry.Image.FirstOrDefaultByName(code);

      int idx = -1;
      string registryImgPath = "";

      if (res == null)
      {
        idx = LaTeXUtils.AddImageToRegistry(filePath, latexCode.ToBase64());
        registryImgPath = Svc.SM.Registry.Image[idx].GetFilePath();
      }
      else
      {
        registryImgPath = res.GetFilePath();
        File.Copy(filePath, registryImgPath, true);
      }

      (var w, var h, var v) = GetMetrics(filePath);

      var imgTag = string.Format(CultureInfo.InvariantCulture,
                           Config.LaTeXImageTag,
                           0,
                           h,
                           registryImgPath,
                           latexCode.ToBase64(),
                           "",
                           v);

      return imgTag;
    }

    private Size GetImageSize(string filePath)
    {
      try
      {
        using (var img = Image.FromFile(filePath))
          return img.Size;
      }
      catch
      {
        // Ignore
      }

      return GetSvgSize(filePath);
    }

    private Size GetSvgSize(string filePath)
    {
      XDocument doc = XDocument.Load(filePath);

      if (doc == null)
        throw new ArgumentException($"Output format unsupported \"{filePath}\".");

      var svg = doc.Element("svg");

      if (svg == null)
        throw new ArgumentException($"Output format unsupported \"{filePath}\". Can't find 'svg' element.");

      var widthStr  = svg.Attribute("width")?.Value;
      var heightStr = svg.Attribute("height")?.Value;

      if (widthStr == null || heightStr == null)
        throw new ArgumentException($"Output format unsupported \"{filePath}\". Can't find 'width' and 'height' attributes.");

      var widthRegexRes  = LaTeXConst.RE.SvgPxDimension.Match(widthStr);
      var heightRegexRes = LaTeXConst.RE.SvgPxDimension.Match(heightStr);

      if (widthRegexRes.Success == false || heightRegexRes.Success == false)
        throw new ArgumentException(
          $"Output format unsupported \"{filePath}\". Unknown format for 'width' and 'height' attributes -- should be in '[\\d]+px' format.");

      return new Size(int.Parse(widthRegexRes.Groups[1].Value, CultureInfo.InvariantCulture),
                      int.Parse(heightRegexRes.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private string GenerateErrorHtml(string html,
                                     string error)
    {
      error = LaTeXUtils.TextToHtml(error ?? string.Empty);

      return html + string.Format(CultureInfo.InvariantCulture,
                                  LaTeXConst.Html.LaTeXError,
                                  error);
    }

    private IEnumerable<(bool success, string imgHtmlOrError, string originalHtml)> GenerateImages(
      LaTeXTag        tag,
      MatchCollection matches)
    {
      List<(bool, string, string)> ret = new List<(bool, string, string)>();

      foreach (Match match in matches)
      {
        string originalHtml = match.Groups[0].Value;
        string latexCode    = match.Groups[1].Value;

        try
        {
          latexCode = LaTeXUtils.PlainText(latexCode);

          var (success, imgHtmlOrError) = LaTeXUtils.GenerateDviFile(Config, tag, latexCode);

          if (success == false)
          {
            ret.Add((false, imgHtmlOrError, originalHtml));
            continue;
          }

          (success, imgHtmlOrError) = LaTeXUtils.GenerateImgFile(Config);

          if (success && string.IsNullOrWhiteSpace(imgHtmlOrError))
          {
            ret.Add((false,
                     "An unknown error occured, make sure your TeX installation has all the required packages, "
                     + "or set it to install missing packages on-the-fly",
                     originalHtml));
          }

          imgHtmlOrError = GenerateImgHtml(imgHtmlOrError,
                                           tag.SurroundTexWith(latexCode));

          ret.Add((success, imgHtmlOrError, originalHtml));
         
        }
        catch (Exception ex)
        {
          ret.Add((false, ex.Message, originalHtml));
        }
      }

      return ret;
    }

    private List<(string html, string latex)> GetAllImagesLaTeXCode()
    {
      List<(string, string)> ret     = new List<(string, string)>();
      var                       matches = LaTeXConst.RE.LaTeXImage.Matches(Selection);
      
      foreach (Match imgMatch in matches)
      {
        var html      = imgMatch.Groups[0].Value;
        var latexCode = LaTeXConst.RE.LaTeXImageLaTeXCode.Match(html);

        if (latexCode.Success)
          ret.Add((html, latexCode.Groups[1].Value));
      }

      return ret;
    }

    #endregion
  }
}
